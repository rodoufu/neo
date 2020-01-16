using Akka.Actor;
using Neo.Ledger;
using Neo.Network.P2P;
using Neo.Network.P2P.Payloads;
using Neo.Persistence;
using Neo.Wallets;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Neo.Cryptography;
using Neo.IO;
using Neo.Plugins;
using Neo.SmartContract.Native;

namespace Neo.Consensus
{
    public sealed partial class ConsensusService
    {
        public class Start { public bool IgnoreRecoveryLogs; }
        public class SetViewNumber { public byte ViewNumber; }
        internal class Timer { public uint Height; public byte ViewNumber; }

        private readonly NeoContainer neoContainer;
        private readonly ConsensusContext context;
        private IActorRef localNode => neoContainer.LocalNodeActor;
        private IActorRef taskManager => neoContainer.TaskManagerActor;
        private LocalNode theLocalNode => neoContainer.LocalNode;
        private Blockchain blockchain => neoContainer.Blockchain;
        private ICancelable timer_token;
        private DateTime block_received_time;
        private bool started = false;

        /// <summary>
        /// This will record the information from last scheduled timer
        /// </summary>
        private DateTime clock_started = TimeProvider.Current.UtcNow;
        private TimeSpan expected_delay = TimeSpan.Zero;

        /// <summary>
        /// This will be cleared every block (so it will not grow out of control, but is used to prevent repeatedly
        /// responding to the same message.
        /// </summary>
        private readonly HashSet<UInt256> knownHashes = new HashSet<UInt256>();
        /// <summary>
        /// This variable is only true during OnRecoveryMessageReceived
        /// </summary>
        private bool isRecovering = false;

        public ConsensusService(NeoContainer neoContainer, IStore store, Wallet wallet)
        {
            this.neoContainer = neoContainer;
            this.context = neoContainer.ResolveConsensusContext(store, wallet);
        }

        private bool AddTransaction(Transaction tx, bool verify, IActorRef self, IUntypedActorContext ctx)
        {
            if (verify && tx.Verify(context.Snapshot, context.SendersFeeMonitor.GetSenderFee(tx.Sender)) != RelayResultReason.Succeed)
            {
                Log($"Invalid transaction: {tx.Hash}{Environment.NewLine}{tx.ToArray().ToHexString()}", LogLevel.Warning);
                RequestChangeView(ChangeViewReason.TxInvalid, self, ctx);
                return false;
            }
            if (!NativeContract.Policy.CheckPolicy(tx, context.Snapshot))
            {
                Log($"reject tx: {tx.Hash}{Environment.NewLine}{tx.ToArray().ToHexString()}", LogLevel.Warning);
                RequestChangeView(ChangeViewReason.TxRejectedByPolicy, self, ctx);
                return false;
            }
            context.Transactions[tx.Hash] = tx;
            context.SendersFeeMonitor.AddSenderFee(tx);
            return CheckPrepareResponse(self, ctx);
        }

        private bool CheckPrepareResponse(IActorRef self, IUntypedActorContext ctx)
        {
            if (context.TransactionHashes.Length == context.Transactions.Count)
            {
                // if we are the primary for this view, but acting as a backup because we recovered our own
                // previously sent prepare request, then we don't want to send a prepare response.
                if (context.IsPrimary || context.WatchOnly) return true;

                // Check maximum block size via Native Contract policy
                if (context.GetExpectedBlockSize() > NativeContract.Policy.GetMaxBlockSize(context.Snapshot))
                {
                    Log($"rejected block: {context.Block.Index}{Environment.NewLine} The size exceed the policy", LogLevel.Warning);
                    RequestChangeView(ChangeViewReason.BlockRejectedByPolicy, self, ctx);
                    return false;
                }

                // Timeout extension due to prepare response sent
                // around 2*15/M=30.0/5 ~ 40% block time (for M=5)
                ExtendTimerByFactor(2, self, ctx);

                Log($"send prepare response");
                localNode.Tell(new LocalNode.SendDirectly { Inventory = context.MakePrepareResponse() });
                CheckPreparations(self, ctx);
            }
            return true;
        }

        private void ChangeTimer(TimeSpan delay, IActorRef self, IUntypedActorContext context)
        {
            clock_started = TimeProvider.Current.UtcNow;
            expected_delay = delay;
            timer_token.CancelIfNotNull();
            timer_token = context.System.Scheduler.ScheduleTellOnceCancelable(delay, self, new Timer
            {
                Height = this.context.Block.Index,
                ViewNumber = this.context.ViewNumber
            }, ActorRefs.NoSender);
        }

        private void CheckCommits()
        {
            if (context.CommitPayloads.Count(p => p?.ConsensusMessage.ViewNumber == context.ViewNumber) >= context.M && context.TransactionHashes.All(p => context.Transactions.ContainsKey(p)))
            {
                Block block = context.CreateBlock();
                Log($"relay block: height={block.Index} hash={block.Hash} tx={block.Transactions.Length}");
                localNode.Tell(new LocalNode.Relay { Inventory = block });
            }
        }

        private void CheckExpectedView(byte viewNumber, IActorRef self, IUntypedActorContext ctx)
        {
            if (context.ViewNumber >= viewNumber) return;
            // if there are `M` change view payloads with NewViewNumber greater than viewNumber, then, it is safe to move
            if (context.ChangeViewPayloads.Count(p => p != null && p.GetDeserializedMessage<ChangeView>().NewViewNumber >= viewNumber) >= context.M)
            {
                if (!context.WatchOnly)
                {
                    ChangeView message = context.ChangeViewPayloads[context.MyIndex]?.GetDeserializedMessage<ChangeView>();
                    // Communicate the network about my agreement to move to `viewNumber`
                    // if my last change view payload, `message`, has NewViewNumber lower than current view to change
                    if (message is null || message.NewViewNumber < viewNumber)
                        localNode.Tell(new LocalNode.SendDirectly { Inventory = context.MakeChangeView(ChangeViewReason.ChangeAgreement) });
                }
                InitializeConsensus(viewNumber, self, ctx);
            }
        }

        private void CheckPreparations(IActorRef self, IUntypedActorContext ctx)
        {
            if (context.PreparationPayloads.Count(p => p != null) >= context.M && context.TransactionHashes.All(p => context.Transactions.ContainsKey(p)))
            {
                ConsensusPayload payload = context.MakeCommit();
                Log($"send commit");
                context.Save();
                localNode.Tell(new LocalNode.SendDirectly { Inventory = payload });
                // Set timer, so we will resend the commit in case of a networking issue
                ChangeTimer(TimeSpan.FromMilliseconds(Blockchain.MillisecondsPerBlock), self, ctx);
                CheckCommits();
            }
        }

        private void InitializeConsensus(byte viewNumber, IActorRef self, IUntypedActorContext ctx)
        {
            context.Reset(viewNumber);
            if (viewNumber > 0)
                Log($"changeview: view={viewNumber} primary={context.Validators[context.GetPrimaryIndex((byte)(viewNumber - 1u))]}", LogLevel.Warning);
            Log($"initialize: height={context.Block.Index} view={viewNumber} index={context.MyIndex} role={(context.IsPrimary ? "Primary" : context.WatchOnly ? "WatchOnly" : "Backup")}");
            if (context.WatchOnly) return;
            if (context.IsPrimary)
            {
                if (isRecovering)
                {
                    ChangeTimer(TimeSpan.FromMilliseconds(Blockchain.MillisecondsPerBlock << (viewNumber + 1)), self, ctx);
                }
                else
                {
                    TimeSpan span = TimeProvider.Current.UtcNow - block_received_time;
                    if (span >= Blockchain.TimePerBlock)
                        ChangeTimer(TimeSpan.Zero, self, ctx);
                    else
                        ChangeTimer(Blockchain.TimePerBlock - span, self, ctx);
                }
            }
            else
            {
                ChangeTimer(TimeSpan.FromMilliseconds(Blockchain.MillisecondsPerBlock << (viewNumber + 1)), self, ctx);
            }
        }

        private void Log(string message, LogLevel level = LogLevel.Info)
        {
            Utility.Log(nameof(ConsensusService), level, message);
        }

        private void OnChangeViewReceived(ConsensusPayload payload, ChangeView message, IActorRef self, IUntypedActorContext ctx)
        {
            if (message.NewViewNumber <= context.ViewNumber)
                OnRecoveryRequestReceived(payload);

            if (context.CommitSent) return;

            var expectedView = context.ChangeViewPayloads[payload.ValidatorIndex]?.GetDeserializedMessage<ChangeView>().NewViewNumber ?? (byte)0;
            if (message.NewViewNumber <= expectedView)
                return;

            Log($"{nameof(OnChangeViewReceived)}: height={payload.BlockIndex} view={message.ViewNumber} index={payload.ValidatorIndex} nv={message.NewViewNumber} reason={message.Reason}");
            context.ChangeViewPayloads[payload.ValidatorIndex] = payload;
            CheckExpectedView(message.NewViewNumber, self, ctx);
        }

        private void OnCommitReceived(ConsensusPayload payload, Commit commit, IActorRef self, IUntypedActorContext ctx)
        {
            ref ConsensusPayload existingCommitPayload = ref context.CommitPayloads[payload.ValidatorIndex];
            if (existingCommitPayload != null)
            {
                if (existingCommitPayload.Hash != payload.Hash)
                    Log($"{nameof(OnCommitReceived)}: different commit from validator! height={payload.BlockIndex} index={payload.ValidatorIndex} view={commit.ViewNumber} existingView={existingCommitPayload.ConsensusMessage.ViewNumber}", LogLevel.Warning);
                return;
            }

            // Timeout extension: commit has been received with success
            // around 4*15s/M=60.0s/5=12.0s ~ 80% block time (for M=5)
            ExtendTimerByFactor(4, self, ctx);

            if (commit.ViewNumber == context.ViewNumber)
            {
                Log($"{nameof(OnCommitReceived)}: height={payload.BlockIndex} view={commit.ViewNumber} index={payload.ValidatorIndex} nc={context.CountCommitted} nf={context.CountFailed}");

                byte[] hashData = context.EnsureHeader()?.GetHashData();
                if (hashData == null)
                {
                    existingCommitPayload = payload;
                }
                else if (Crypto.VerifySignature(hashData, commit.Signature,
                    context.Validators[payload.ValidatorIndex].EncodePoint(false)))
                {
                    existingCommitPayload = payload;
                    CheckCommits();
                }
                return;
            }
            // Receiving commit from another view
            Log($"{nameof(OnCommitReceived)}: record commit for different view={commit.ViewNumber} index={payload.ValidatorIndex} height={payload.BlockIndex}");
            existingCommitPayload = payload;
        }

        // this function increases existing timer (never decreases) with a value proportional to `maxDelayInBlockTimes`*`Blockchain.MillisecondsPerBlock`
        private void ExtendTimerByFactor(int maxDelayInBlockTimes, IActorRef self, IUntypedActorContext ctx)
        {
            TimeSpan nextDelay = expected_delay - (TimeProvider.Current.UtcNow - clock_started) + TimeSpan.FromMilliseconds(maxDelayInBlockTimes * Blockchain.MillisecondsPerBlock / context.M);
            if (!context.WatchOnly && !context.ViewChanging && !context.CommitSent && (nextDelay > TimeSpan.Zero))
                ChangeTimer(nextDelay, self, ctx);
        }

        private void OnConsensusPayload(ConsensusPayload payload, IActorRef self, IUntypedActorContext ctx)
        {
            if (context.BlockSent) return;
            if (payload.Version != context.Block.Version) return;
            if (payload.PrevHash != context.Block.PrevHash || payload.BlockIndex != context.Block.Index)
            {
                if (context.Block.Index < payload.BlockIndex)
                {
                    Log($"chain sync: expected={payload.BlockIndex} current={context.Block.Index - 1} nodes={theLocalNode.ConnectedCount}", LogLevel.Warning);
                }
                return;
            }
            if (payload.ValidatorIndex >= context.Validators.Length) return;
            ConsensusMessage message;
            try
            {
                message = payload.ConsensusMessage;
            }
            catch (FormatException)
            {
                return;
            }
            catch (IOException)
            {
                return;
            }
            context.LastSeenMessage[payload.ValidatorIndex] = (int)payload.BlockIndex;
            foreach (IP2PPlugin plugin in Plugin.P2PPlugins)
                if (!plugin.OnConsensusMessage(payload))
                    return;
            switch (message)
            {
                case ChangeView view:
                    OnChangeViewReceived(payload, view, self, ctx);
                    break;
                case PrepareRequest request:
                    OnPrepareRequestReceived(payload, request, self, ctx);
                    break;
                case PrepareResponse response:
                    OnPrepareResponseReceived(payload, response, self, ctx);
                    break;
                case Commit commit:
                    OnCommitReceived(payload, commit, self, ctx);
                    break;
                case RecoveryRequest _:
                    OnRecoveryRequestReceived(payload);
                    break;
                case RecoveryMessage recovery:
                    OnRecoveryMessageReceived(payload, recovery, self, ctx);
                    break;
            }
        }

        private void OnPersistCompleted(Block block, IActorRef self, IUntypedActorContext ctx)
        {
            Log($"persist block: height={block.Index} hash={block.Hash} tx={block.Transactions.Length}");
            block_received_time = TimeProvider.Current.UtcNow;
            knownHashes.Clear();
            InitializeConsensus(0, self, ctx);
        }

        private void OnRecoveryMessageReceived(ConsensusPayload payload, RecoveryMessage message, IActorRef self, IUntypedActorContext ctx)
        {
            // isRecovering is always set to false again after OnRecoveryMessageReceived
            isRecovering = true;
            int validChangeViews = 0, totalChangeViews = 0, validPrepReq = 0, totalPrepReq = 0;
            int validPrepResponses = 0, totalPrepResponses = 0, validCommits = 0, totalCommits = 0;

            Log($"{nameof(OnRecoveryMessageReceived)}: height={payload.BlockIndex} view={message.ViewNumber} index={payload.ValidatorIndex}");
            try
            {
                if (message.ViewNumber > context.ViewNumber)
                {
                    if (context.CommitSent) return;
                    ConsensusPayload[] changeViewPayloads = message.GetChangeViewPayloads(context, payload);
                    totalChangeViews = changeViewPayloads.Length;
                    foreach (ConsensusPayload changeViewPayload in changeViewPayloads)
                        if (ReverifyAndProcessPayload(changeViewPayload, self, ctx)) validChangeViews++;
                }
                if (message.ViewNumber == context.ViewNumber && !context.NotAcceptingPayloadsDueToViewChanging && !context.CommitSent)
                {
                    if (!context.RequestSentOrReceived)
                    {
                        ConsensusPayload prepareRequestPayload = message.GetPrepareRequestPayload(context, payload);
                        if (prepareRequestPayload != null)
                        {
                            totalPrepReq = 1;
                            if (ReverifyAndProcessPayload(prepareRequestPayload, self, ctx)) validPrepReq++;
                        }
                        else if (context.IsPrimary)
                            SendPrepareRequest(self, ctx);
                    }
                    ConsensusPayload[] prepareResponsePayloads = message.GetPrepareResponsePayloads(context, payload);
                    totalPrepResponses = prepareResponsePayloads.Length;
                    foreach (ConsensusPayload prepareResponsePayload in prepareResponsePayloads)
                        if (ReverifyAndProcessPayload(prepareResponsePayload, self, ctx)) validPrepResponses++;
                }
                if (message.ViewNumber <= context.ViewNumber)
                {
                    // Ensure we know about all commits from lower view numbers.
                    ConsensusPayload[] commitPayloads = message.GetCommitPayloadsFromRecoveryMessage(context, payload);
                    totalCommits = commitPayloads.Length;
                    foreach (ConsensusPayload commitPayload in commitPayloads)
                        if (ReverifyAndProcessPayload(commitPayload, self, ctx)) validCommits++;
                }
            }
            finally
            {
                Log($"{nameof(OnRecoveryMessageReceived)}: finished (valid/total) " +
                    $"ChgView: {validChangeViews}/{totalChangeViews} " +
                    $"PrepReq: {validPrepReq}/{totalPrepReq} " +
                    $"PrepResp: {validPrepResponses}/{totalPrepResponses} " +
                    $"Commits: {validCommits}/{totalCommits}");
                isRecovering = false;
            }
        }

        private void OnRecoveryRequestReceived(ConsensusPayload payload)
        {
            // We keep track of the payload hashes received in this block, and don't respond with recovery
            // in response to the same payload that we already responded to previously.
            // ChangeView messages include a Timestamp when the change view is sent, thus if a node restarts
            // and issues a change view for the same view, it will have a different hash and will correctly respond
            // again; however replay attacks of the ChangeView message from arbitrary nodes will not trigger an
            // additional recovery message response.
            if (!knownHashes.Add(payload.Hash)) return;

            Log($"On{payload.ConsensusMessage.GetType().Name}Received: height={payload.BlockIndex} index={payload.ValidatorIndex} view={payload.ConsensusMessage.ViewNumber}");
            if (context.WatchOnly) return;
            if (!context.CommitSent)
            {
                bool shouldSendRecovery = false;
                int allowedRecoveryNodeCount = context.F;
                // Limit recoveries to be sent from an upper limit of `f` nodes
                for (int i = 1; i <= allowedRecoveryNodeCount; i++)
                {
                    var chosenIndex = (payload.ValidatorIndex + i) % context.Validators.Length;
                    if (chosenIndex != context.MyIndex) continue;
                    shouldSendRecovery = true;
                    break;
                }

                if (!shouldSendRecovery) return;
            }
            Log($"send recovery: view={context.ViewNumber}");
            localNode.Tell(new LocalNode.SendDirectly { Inventory = context.MakeRecoveryMessage() });
        }

        private void OnPrepareRequestReceived(ConsensusPayload payload, PrepareRequest message, IActorRef self, IUntypedActorContext ctx)
        {
            if (context.RequestSentOrReceived || context.NotAcceptingPayloadsDueToViewChanging) return;
            if (payload.ValidatorIndex != context.Block.ConsensusData.PrimaryIndex || message.ViewNumber != context.ViewNumber) return;
            Log($"{nameof(OnPrepareRequestReceived)}: height={payload.BlockIndex} view={message.ViewNumber} index={payload.ValidatorIndex} tx={message.TransactionHashes.Length}");
            if (message.Timestamp <= context.PrevHeader.Timestamp || message.Timestamp > TimeProvider.Current.UtcNow.AddMilliseconds(8 * Blockchain.MillisecondsPerBlock).ToTimestampMS())
            {
                Log($"Timestamp incorrect: {message.Timestamp}", LogLevel.Warning);
                return;
            }
            if (message.TransactionHashes.Any(p => context.Snapshot.ContainsTransaction(p)))
            {
                Log($"Invalid request: transaction already exists", LogLevel.Warning);
                return;
            }

            // Timeout extension: prepare request has been received with success
            // around 2*15/M=30.0/5 ~ 40% block time (for M=5)
            ExtendTimerByFactor(2, self, ctx);

            context.Block.Timestamp = message.Timestamp;
            context.Block.ConsensusData.Nonce = message.Nonce;
            context.TransactionHashes = message.TransactionHashes;
            context.Transactions = new Dictionary<UInt256, Transaction>();
            context.SendersFeeMonitor = new SendersFeeMonitor();
            for (int i = 0; i < context.PreparationPayloads.Length; i++)
                if (context.PreparationPayloads[i] != null)
                    if (!context.PreparationPayloads[i].GetDeserializedMessage<PrepareResponse>().PreparationHash.Equals(payload.Hash))
                        context.PreparationPayloads[i] = null;
            context.PreparationPayloads[payload.ValidatorIndex] = payload;
            byte[] hashData = context.EnsureHeader().GetHashData();
            for (int i = 0; i < context.CommitPayloads.Length; i++)
                if (context.CommitPayloads[i]?.ConsensusMessage.ViewNumber == context.ViewNumber)
                    if (!Crypto.VerifySignature(hashData, context.CommitPayloads[i].GetDeserializedMessage<Commit>().Signature, context.Validators[i].EncodePoint(false)))
                        context.CommitPayloads[i] = null;

            if (context.TransactionHashes.Length == 0)
            {
                // There are no tx so we should act like if all the transactions were filled
                CheckPrepareResponse(self, ctx);
                return;
            }

            Dictionary<UInt256, Transaction> mempoolVerified = blockchain.MemPool.GetVerifiedTransactions().ToDictionary(p => p.Hash);
            List<Transaction> unverified = new List<Transaction>();
            foreach (UInt256 hash in context.TransactionHashes)
            {
                if (mempoolVerified.TryGetValue(hash, out Transaction tx))
                {
                    if (!AddTransaction(tx, false, self, ctx))
                        return;
                }
                else
                {
                    if (blockchain.MemPool.TryGetValue(hash, out tx))
                        unverified.Add(tx);
                }
            }
            foreach (Transaction tx in unverified)
                if (!AddTransaction(tx, true, self, ctx))
                    return;
            if (context.Transactions.Count < context.TransactionHashes.Length)
            {
                UInt256[] hashes = context.TransactionHashes.Where(i => !context.Transactions.ContainsKey(i)).ToArray();
                taskManager.Tell(new TaskManager.RestartTasks
                {
                    Payload = InvPayload.Create(InventoryType.TX, hashes)
                });
            }
        }

        private void OnPrepareResponseReceived(ConsensusPayload payload, PrepareResponse message, IActorRef self, IUntypedActorContext ctx)
        {
            if (message.ViewNumber != context.ViewNumber) return;
            if (context.PreparationPayloads[payload.ValidatorIndex] != null || context.NotAcceptingPayloadsDueToViewChanging) return;
            if (context.PreparationPayloads[context.Block.ConsensusData.PrimaryIndex] != null && !message.PreparationHash.Equals(context.PreparationPayloads[context.Block.ConsensusData.PrimaryIndex].Hash))
                return;

            // Timeout extension: prepare response has been received with success
            // around 2*15/M=30.0/5 ~ 40% block time (for M=5)
            ExtendTimerByFactor(2, self, ctx);

            Log($"{nameof(OnPrepareResponseReceived)}: height={payload.BlockIndex} view={message.ViewNumber} index={payload.ValidatorIndex}");
            context.PreparationPayloads[payload.ValidatorIndex] = payload;
            if (context.WatchOnly || context.CommitSent) return;
            if (context.RequestSentOrReceived)
                CheckPreparations(self, ctx);
        }

        private void RequestRecovery()
        {
            if (context.Block.Index == blockchain.HeaderHeight + 1)
                localNode.Tell(new LocalNode.SendDirectly { Inventory = context.MakeRecoveryRequest() });
        }

        private void OnStart(Start options, IActorRef sender, IActorRef self, IUntypedActorContext ctx)
        {
            Log("OnStart");
            started = true;
            if (!options.IgnoreRecoveryLogs && context.Load())
            {
                if (context.Transactions != null)
                {
                    sender.Ask<Blockchain.FillCompleted>(new Blockchain.FillMemoryPool
                    {
                        Transactions = context.Transactions.Values
                    }).Wait();
                }
                if (context.CommitSent)
                {
                    CheckPreparations(self, ctx);
                    return;
                }
            }
            InitializeConsensus(0, self, ctx);
            // Issue a ChangeView with NewViewNumber of 0 to request recovery messages on start-up.
            if (!context.WatchOnly)
                RequestRecovery();
        }

        private void OnTimer(Timer timer, IActorRef self, IUntypedActorContext ctx)
        {
            if (context.WatchOnly || context.BlockSent) return;
            if (timer.Height != context.Block.Index || timer.ViewNumber != context.ViewNumber) return;
            Log($"timeout: height={timer.Height} view={timer.ViewNumber}");
            if (context.IsPrimary && !context.RequestSentOrReceived)
            {
                SendPrepareRequest(self, ctx);
            }
            else if ((context.IsPrimary && context.RequestSentOrReceived) || context.IsBackup)
            {
                if (context.CommitSent)
                {
                    // Re-send commit periodically by sending recover message in case of a network issue.
                    Log($"send recovery to resend commit");
                    localNode.Tell(new LocalNode.SendDirectly { Inventory = context.MakeRecoveryMessage() });
                    ChangeTimer(TimeSpan.FromMilliseconds(Blockchain.MillisecondsPerBlock << 1), self, ctx);
                }
                else
                {
                    var reason = ChangeViewReason.Timeout;

                    if (context.Block != null && context.TransactionHashes?.Length > context.Transactions?.Count)
                    {
                        reason = ChangeViewReason.TxNotFound;
                    }

                    RequestChangeView(reason, self, ctx);
                }
            }
        }

        private void OnTransaction(Transaction transaction, IActorRef self, IUntypedActorContext ctx)
        {
            if (!context.IsBackup || context.NotAcceptingPayloadsDueToViewChanging || !context.RequestSentOrReceived || context.ResponseSent || context.BlockSent)
                return;
            if (context.Transactions.ContainsKey(transaction.Hash)) return;
            if (!context.TransactionHashes.Contains(transaction.Hash)) return;
            AddTransaction(transaction, true, self, ctx);
        }

        private void RequestChangeView(ChangeViewReason reason, IActorRef self, IUntypedActorContext ctx)
        {
            if (context.WatchOnly) return;
            // Request for next view is always one view more than the current context.ViewNumber
            // Nodes will not contribute for changing to a view higher than (context.ViewNumber+1), unless they are recovered
            // The latter may happen by nodes in higher views with, at least, `M` proofs
            byte expectedView = context.ViewNumber;
            expectedView++;
            ChangeTimer(TimeSpan.FromMilliseconds(Blockchain.MillisecondsPerBlock << (expectedView + 1)), self, ctx);
            if ((context.CountCommitted + context.CountFailed) > context.F)
            {
                Log($"skip requesting change view: height={context.Block.Index} view={context.ViewNumber} nv={expectedView} nc={context.CountCommitted} nf={context.CountFailed} reason={reason}");
                RequestRecovery();
                return;
            }
            Log($"request change view: height={context.Block.Index} view={context.ViewNumber} nv={expectedView} nc={context.CountCommitted} nf={context.CountFailed} reason={reason}");
            localNode.Tell(new LocalNode.SendDirectly { Inventory = context.MakeChangeView(reason) });
            CheckExpectedView(expectedView, self, ctx);
        }

        private bool ReverifyAndProcessPayload(ConsensusPayload payload, IActorRef self, IUntypedActorContext ctx)
        {
            if (!payload.Verify(context.Snapshot)) return false;
            OnConsensusPayload(payload, self, ctx);
            return true;
        }

        private void SendPrepareRequest(IActorRef self, IUntypedActorContext ctx)
        {
            Log($"send prepare request: height={context.Block.Index} view={context.ViewNumber}");
            localNode.Tell(new LocalNode.SendDirectly { Inventory = context.MakePrepareRequest() });

            if (context.Validators.Length == 1)
                CheckPreparations(self, ctx);

            if (context.TransactionHashes.Length > 0)
            {
                foreach (InvPayload payload in InvPayload.CreateGroup(InventoryType.TX, context.TransactionHashes))
                    localNode.Tell(Message.Create(MessageCommand.Inv, payload));
            }
            ChangeTimer(TimeSpan.FromMilliseconds((Blockchain.MillisecondsPerBlock << (context.ViewNumber + 1)) - (context.ViewNumber == 0 ? Blockchain.MillisecondsPerBlock : 0)), self, ctx);
        }

    }

}
