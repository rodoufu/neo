using Akka.Actor;
using Akka.Configuration;
using Neo.Cryptography;
using Neo.IO;
using Neo.IO.Actors;
using Neo.Ledger;
using Neo.Network.P2P;
using Neo.Network.P2P.Payloads;
using Neo.Persistence;
using Neo.Plugins;
using Neo.SmartContract.Native;
using Neo.Wallets;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autofac;

namespace Neo.Consensus
{
    public sealed partial class ConsensusService
    {
        public sealed class ConsensusServiceActor : UntypedActor
        {
            private ConsensusService consensusService;

            public ConsensusServiceActor(ConsensusService consensusService)
            {
                this.consensusService = consensusService;
                Context.System.EventStream.Subscribe(Self, typeof(Blockchain.PersistCompleted));
            }

            protected override void OnReceive(object message)
            {
                if (message is ConsensusService.Start options)
                {
                    if (consensusService.started) return;
                    OnStart(options);
                }
                else
                {
                    if (!consensusService.started) return;
                    switch (message)
                    {
                        case SetViewNumber setView:
                            InitializeConsensus(setView.ViewNumber);
                            break;
                        case Timer timer:
                            OnTimer(timer);
                            break;
                        case ConsensusPayload payload:
                            OnConsensusPayload(payload);
                            break;
                        case Transaction transaction:
                            OnTransaction(transaction);
                            break;
                        case Blockchain.PersistCompleted completed:
                            OnPersistCompleted(completed.Block);
                            break;
                    }
                }
            }

            private void OnStart(Start options)
            {
                consensusService.Log("OnStart");
                consensusService.started = true;
                var context = consensusService.context;
                if (!options.IgnoreRecoveryLogs && context.Load())
                {
                    if (context.Transactions != null)
                    {
                        Sender.Ask<Blockchain.FillCompleted>(new Blockchain.FillMemoryPool
                        {
                            Transactions = context.Transactions.Values
                        }).Wait();
                    }

                    if (context.CommitSent)
                    {
                        CheckPreparations();
                        return;
                    }
                }

                InitializeConsensus(0);
                // Issue a ChangeView with NewViewNumber of 0 to request recovery messages on start-up.
                if (!context.WatchOnly)
                    consensusService.RequestRecovery();
            }

            private void InitializeConsensus(byte viewNumber)
            {
                var context = consensusService.context;
                context.Reset(viewNumber);
                if (viewNumber > 0)
                    consensusService.Log(
                        $"changeview: view={viewNumber} primary={context.Validators[context.GetPrimaryIndex((byte) (viewNumber - 1u))]}",
                        LogLevel.Warning);
                consensusService.Log(
                    $"initialize: height={context.Block.Index} view={viewNumber} index={context.MyIndex} role={(context.IsPrimary ? "Primary" : context.WatchOnly ? "WatchOnly" : "Backup")}");
                if (context.WatchOnly) return;
                if (context.IsPrimary)
                {
                    if (consensusService.isRecovering)
                    {
                        ChangeTimer(
                            TimeSpan.FromMilliseconds(Blockchain.MillisecondsPerBlock << (viewNumber + 1)));
                    }
                    else
                    {
                        TimeSpan span = TimeProvider.Current.UtcNow - consensusService.block_received_time;
                        if (span >= Blockchain.TimePerBlock)
                            ChangeTimer(TimeSpan.Zero);
                        else
                            ChangeTimer(Blockchain.TimePerBlock - span);
                    }
                }
                else
                {
                    ChangeTimer(
                        TimeSpan.FromMilliseconds(Blockchain.MillisecondsPerBlock << (viewNumber + 1)));
                }
            }

            private void OnTimer(Timer timer)
            {
                var context = consensusService.context;
                if (context.WatchOnly || context.BlockSent) return;
                if (timer.Height != context.Block.Index || timer.ViewNumber != context.ViewNumber) return;
                consensusService.Log($"timeout: height={timer.Height} view={timer.ViewNumber}");
                if (context.IsPrimary && !context.RequestSentOrReceived)
                {
                    SendPrepareRequest();
                }
                else if ((context.IsPrimary && context.RequestSentOrReceived) || context.IsBackup)
                {
                    if (context.CommitSent)
                    {
                        // Re-send commit periodically by sending recover message in case of a network issue.
                        consensusService.Log($"send recovery to resend commit");
                        consensusService.localNode.Tell(new LocalNode.SendDirectly
                            {Inventory = context.MakeRecoveryMessage()});
                        ChangeTimer(TimeSpan.FromMilliseconds(Blockchain.MillisecondsPerBlock << 1));
                    }
                    else
                    {
                        var reason = ChangeViewReason.Timeout;

                        if (context.Block != null && context.TransactionHashes?.Length > context.Transactions?.Count)
                        {
                            reason = ChangeViewReason.TxNotFound;
                        }

                        RequestChangeView(reason);
                    }
                }
            }

            private void OnConsensusPayload(ConsensusPayload payload)
            {
                var context = consensusService.context;
                if (context.BlockSent) return;
                if (payload.Version != context.Block.Version) return;
                if (payload.PrevHash != context.Block.PrevHash || payload.BlockIndex != context.Block.Index)
                {
                    if (context.Block.Index < payload.BlockIndex)
                    {
                        consensusService.Log(
                            $"chain sync: expected={payload.BlockIndex} current={context.Block.Index - 1} nodes={consensusService.theLocalNode.ConnectedCount}",
                            LogLevel.Warning);
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

                context.LastSeenMessage[payload.ValidatorIndex] = (int) payload.BlockIndex;
                foreach (IP2PPlugin plugin in Plugin.P2PPlugins)
                    if (!plugin.OnConsensusMessage(payload))
                        return;
                switch (message)
                {
                    case ChangeView view:
                        OnChangeViewReceived(payload, view);
                        break;
                    case PrepareRequest request:
                        OnPrepareRequestReceived(payload, request);
                        break;
                    case PrepareResponse response:
                        OnPrepareResponseReceived(payload, response);
                        break;
                    case Commit commit:
                        OnCommitReceived(payload, commit);
                        break;
                    case RecoveryRequest _:
                        OnRecoveryRequestReceived(payload);
                        break;
                    case RecoveryMessage recovery:
                        OnRecoveryMessageReceived(payload, recovery);
                        break;
                }
            }

            private void OnTransaction(Transaction transaction)
            {
                var context = consensusService.context;
                if (!context.IsBackup || context.NotAcceptingPayloadsDueToViewChanging ||
                    !context.RequestSentOrReceived ||
                    context.ResponseSent || context.BlockSent)
                    return;
                if (context.Transactions.ContainsKey(transaction.Hash)) return;
                if (!context.TransactionHashes.Contains(transaction.Hash)) return;
                AddTransaction(transaction, true);
            }

            private void OnPersistCompleted(Block block)
            {
                consensusService.Log(
                    $"persist block: height={block.Index} hash={block.Hash} tx={block.Transactions.Length}");
                consensusService.block_received_time = TimeProvider.Current.UtcNow;
                consensusService.knownHashes.Clear();
                InitializeConsensus(0);
            }

            private void OnChangeViewReceived(ConsensusPayload payload, ChangeView message)
            {
                var context = consensusService.context;
                if (message.NewViewNumber <= context.ViewNumber)
                    OnRecoveryRequestReceived(payload);

                if (context.CommitSent) return;

                var expectedView =
                    context.ChangeViewPayloads[payload.ValidatorIndex]?.GetDeserializedMessage<ChangeView>()
                        .NewViewNumber ?? (byte) 0;
                if (message.NewViewNumber <= expectedView)
                    return;

                consensusService.Log(
                    $"{nameof(OnChangeViewReceived)}: height={payload.BlockIndex} view={message.ViewNumber} index={payload.ValidatorIndex} nv={message.NewViewNumber} reason={message.Reason}");
                context.ChangeViewPayloads[payload.ValidatorIndex] = payload;
                CheckExpectedView(message.NewViewNumber);
            }

            private void OnPrepareRequestReceived(ConsensusPayload payload, PrepareRequest message)
            {
                var context = consensusService.context;
                if (context.RequestSentOrReceived || context.NotAcceptingPayloadsDueToViewChanging) return;
                if (payload.ValidatorIndex != context.Block.ConsensusData.PrimaryIndex ||
                    message.ViewNumber != context.ViewNumber) return;
                consensusService.Log(
                    $"{nameof(OnPrepareRequestReceived)}: height={payload.BlockIndex} view={message.ViewNumber} index={payload.ValidatorIndex} tx={message.TransactionHashes.Length}");
                if (message.Timestamp <= context.PrevHeader.Timestamp || message.Timestamp > TimeProvider.Current.UtcNow
                        .AddMilliseconds(8 * Blockchain.MillisecondsPerBlock).ToTimestampMS())
                {
                    consensusService.Log($"Timestamp incorrect: {message.Timestamp}", LogLevel.Warning);
                    return;
                }

                if (message.TransactionHashes.Any(p => context.Snapshot.ContainsTransaction(p)))
                {
                    consensusService.Log($"Invalid request: transaction already exists", LogLevel.Warning);
                    return;
                }

                // Timeout extension: prepare request has been received with success
                // around 2*15/M=30.0/5 ~ 40% block time (for M=5)
                ExtendTimerByFactor(2);

                context.Block.Timestamp = message.Timestamp;
                context.Block.ConsensusData.Nonce = message.Nonce;
                context.TransactionHashes = message.TransactionHashes;
                context.Transactions = new Dictionary<UInt256, Transaction>();
                context.SendersFeeMonitor = new SendersFeeMonitor();
                for (int i = 0; i < context.PreparationPayloads.Length; i++)
                    if (context.PreparationPayloads[i] != null)
                        if (!context.PreparationPayloads[i].GetDeserializedMessage<PrepareResponse>().PreparationHash
                            .Equals(payload.Hash))
                            context.PreparationPayloads[i] = null;
                context.PreparationPayloads[payload.ValidatorIndex] = payload;
                byte[] hashData = context.EnsureHeader().GetHashData();
                for (int i = 0; i < context.CommitPayloads.Length; i++)
                    if (context.CommitPayloads[i]?.ConsensusMessage.ViewNumber == context.ViewNumber)
                        if (!Crypto.VerifySignature(hashData,
                            context.CommitPayloads[i].GetDeserializedMessage<Commit>().Signature,
                            context.Validators[i].EncodePoint(false)))
                            context.CommitPayloads[i] = null;

                if (context.TransactionHashes.Length == 0)
                {
                    // There are no tx so we should act like if all the transactions were filled
                    CheckPrepareResponse();
                    return;
                }

                Dictionary<UInt256, Transaction> mempoolVerified =
                    consensusService.blockchain.MemPool.GetVerifiedTransactions().ToDictionary(p => p.Hash);
                List<Transaction> unverified = new List<Transaction>();
                foreach (UInt256 hash in context.TransactionHashes)
                {
                    if (mempoolVerified.TryGetValue(hash, out Transaction tx))
                    {
                        if (!AddTransaction(tx, false))
                            return;
                    }
                    else
                    {
                        if (consensusService.blockchain.MemPool.TryGetValue(hash, out tx))
                            unverified.Add(tx);
                    }
                }

                foreach (Transaction tx in unverified)
                    if (!AddTransaction(tx, true))
                        return;
                if (context.Transactions.Count < context.TransactionHashes.Length)
                {
                    UInt256[] hashes = context.TransactionHashes.Where(i => !context.Transactions.ContainsKey(i))
                        .ToArray();
                    consensusService.taskManager.Tell(new TaskManager.RestartTasks
                    {
                        Payload = InvPayload.Create(InventoryType.TX, hashes)
                    });
                }
            }

            private void OnPrepareResponseReceived(ConsensusPayload payload, PrepareResponse message)
            {
                var context = consensusService.context;
                if (message.ViewNumber != context.ViewNumber) return;
                if (context.PreparationPayloads[payload.ValidatorIndex] != null ||
                    context.NotAcceptingPayloadsDueToViewChanging) return;
                if (context.PreparationPayloads[context.Block.ConsensusData.PrimaryIndex] != null &&
                    !message.PreparationHash.Equals(context
                        .PreparationPayloads[context.Block.ConsensusData.PrimaryIndex].Hash))
                    return;

                // Timeout extension: prepare response has been received with success
                // around 2*15/M=30.0/5 ~ 40% block time (for M=5)
                ExtendTimerByFactor(2);

                consensusService.Log(
                    $"{nameof(OnPrepareResponseReceived)}: height={payload.BlockIndex} view={message.ViewNumber} index={payload.ValidatorIndex}");
                context.PreparationPayloads[payload.ValidatorIndex] = payload;
                if (context.WatchOnly || context.CommitSent) return;
                if (context.RequestSentOrReceived)
                    CheckPreparations();
            }

            private void OnCommitReceived(ConsensusPayload payload, Commit commit)
            {
                var context = consensusService.context;
                ref ConsensusPayload existingCommitPayload = ref context.CommitPayloads[payload.ValidatorIndex];
                if (existingCommitPayload != null)
                {
                    if (existingCommitPayload.Hash != payload.Hash)
                        consensusService.Log(
                            $"{nameof(OnCommitReceived)}: different commit from validator! height={payload.BlockIndex} index={payload.ValidatorIndex} view={commit.ViewNumber} existingView={existingCommitPayload.ConsensusMessage.ViewNumber}",
                            LogLevel.Warning);
                    return;
                }

                // Timeout extension: commit has been received with success
                // around 4*15s/M=60.0s/5=12.0s ~ 80% block time (for M=5)
                ExtendTimerByFactor(4);

                if (commit.ViewNumber == context.ViewNumber)
                {
                    consensusService.Log(
                        $"{nameof(OnCommitReceived)}: height={payload.BlockIndex} view={commit.ViewNumber} index={payload.ValidatorIndex} nc={context.CountCommitted} nf={context.CountFailed}");

                    byte[] hashData = context.EnsureHeader()?.GetHashData();
                    if (hashData == null)
                    {
                        existingCommitPayload = payload;
                    }
                    else if (Crypto.VerifySignature(hashData, commit.Signature,
                        context.Validators[payload.ValidatorIndex].EncodePoint(false)))
                    {
                        existingCommitPayload = payload;
                        consensusService.CheckCommits();
                    }

                    return;
                }

                // Receiving commit from another view
                consensusService.Log(
                    $"{nameof(OnCommitReceived)}: record commit for different view={commit.ViewNumber} index={payload.ValidatorIndex} height={payload.BlockIndex}");
                existingCommitPayload = payload;
            }

            private void OnRecoveryRequestReceived(ConsensusPayload payload)
            {
                var context = consensusService.context;
                // We keep track of the payload hashes received in this block, and don't respond with recovery
                // in response to the same payload that we already responded to previously.
                // ChangeView messages include a Timestamp when the change view is sent, thus if a node restarts
                // and issues a change view for the same view, it will have a different hash and will correctly respond
                // again; however replay attacks of the ChangeView message from arbitrary nodes will not trigger an
                // additional recovery message response.
                if (!consensusService.knownHashes.Add(payload.Hash)) return;

                consensusService.Log(
                    $"On{payload.ConsensusMessage.GetType().Name}Received: height={payload.BlockIndex} index={payload.ValidatorIndex} view={payload.ConsensusMessage.ViewNumber}");
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

                consensusService.Log($"send recovery: view={context.ViewNumber}");
                consensusService.localNode.Tell(new LocalNode.SendDirectly {Inventory = context.MakeRecoveryMessage()});
            }

            private void OnRecoveryMessageReceived(ConsensusPayload payload, RecoveryMessage message)
            {
                var context = consensusService.context;
                // isRecovering is always set to false again after OnRecoveryMessageReceived
                consensusService.isRecovering = true;
                int validChangeViews = 0, totalChangeViews = 0, validPrepReq = 0, totalPrepReq = 0;
                int validPrepResponses = 0, totalPrepResponses = 0, validCommits = 0, totalCommits = 0;

                consensusService.Log(
                    $"{nameof(OnRecoveryMessageReceived)}: height={payload.BlockIndex} view={message.ViewNumber} index={payload.ValidatorIndex}");
                try
                {
                    if (message.ViewNumber > context.ViewNumber)
                    {
                        if (context.CommitSent) return;
                        ConsensusPayload[] changeViewPayloads = message.GetChangeViewPayloads(context, payload);
                        totalChangeViews = changeViewPayloads.Length;
                        foreach (ConsensusPayload changeViewPayload in changeViewPayloads)
                            if (ReverifyAndProcessPayload(changeViewPayload))
                                validChangeViews++;
                    }

                    if (message.ViewNumber == context.ViewNumber && !context.NotAcceptingPayloadsDueToViewChanging &&
                        !context.CommitSent)
                    {
                        if (!context.RequestSentOrReceived)
                        {
                            ConsensusPayload prepareRequestPayload = message.GetPrepareRequestPayload(context, payload);
                            if (prepareRequestPayload != null)
                            {
                                totalPrepReq = 1;
                                if (ReverifyAndProcessPayload(prepareRequestPayload)) validPrepReq++;
                            }
                            else if (context.IsPrimary)
                                SendPrepareRequest();
                        }

                        ConsensusPayload[] prepareResponsePayloads =
                            message.GetPrepareResponsePayloads(context, payload);
                        totalPrepResponses = prepareResponsePayloads.Length;
                        foreach (ConsensusPayload prepareResponsePayload in prepareResponsePayloads)
                            if (ReverifyAndProcessPayload(prepareResponsePayload))
                                validPrepResponses++;
                    }

                    if (message.ViewNumber <= context.ViewNumber)
                    {
                        // Ensure we know about all commits from lower view numbers.
                        ConsensusPayload[] commitPayloads =
                            message.GetCommitPayloadsFromRecoveryMessage(context, payload);
                        totalCommits = commitPayloads.Length;
                        foreach (ConsensusPayload commitPayload in commitPayloads)
                            if (ReverifyAndProcessPayload(commitPayload))
                                validCommits++;
                    }
                }
                finally
                {
                    consensusService.Log($"{nameof(OnRecoveryMessageReceived)}: finished (valid/total) " +
                        $"ChgView: {validChangeViews}/{totalChangeViews} " +
                        $"PrepReq: {validPrepReq}/{totalPrepReq} " +
                        $"PrepResp: {validPrepResponses}/{totalPrepResponses} " +
                        $"Commits: {validCommits}/{totalCommits}");
                    consensusService.isRecovering = false;
                }
            }

            private bool ReverifyAndProcessPayload(ConsensusPayload payload)
            {
                if (!payload.Verify(consensusService.context.Snapshot)) return false;
                OnConsensusPayload(payload);
                return true;
            }

            private void CheckExpectedView(byte viewNumber)
            {
                var context = consensusService.context;
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
                            consensusService.localNode.Tell(new LocalNode.SendDirectly { Inventory = context.MakeChangeView(ChangeViewReason.ChangeAgreement) });
                    }
                    InitializeConsensus(viewNumber);
                }
            }

            private void RequestChangeView(ChangeViewReason reason)
            {
                var context = consensusService.context;
                if (context.WatchOnly) return;
                // Request for next view is always one view more than the current context.ViewNumber
                // Nodes will not contribute for changing to a view higher than (context.ViewNumber+1), unless they are recovered
                // The latter may happen by nodes in higher views with, at least, `M` proofs
                byte expectedView = context.ViewNumber;
                expectedView++;
                ChangeTimer(TimeSpan.FromMilliseconds(Blockchain.MillisecondsPerBlock << (expectedView + 1)));
                if ((context.CountCommitted + context.CountFailed) > context.F)
                {
                    consensusService.Log($"skip requesting change view: height={context.Block.Index} view={context.ViewNumber} nv={expectedView} nc={context.CountCommitted} nf={context.CountFailed} reason={reason}");
                    consensusService.RequestRecovery();
                    return;
                }
                consensusService.Log($"request change view: height={context.Block.Index} view={context.ViewNumber} nv={expectedView} nc={context.CountCommitted} nf={context.CountFailed} reason={reason}");
                consensusService.localNode.Tell(new LocalNode.SendDirectly { Inventory = context.MakeChangeView(reason) });
                CheckExpectedView(expectedView);
            }

            // this function increases existing timer (never decreases) with a value proportional to `maxDelayInBlockTimes`*`Blockchain.MillisecondsPerBlock`
            private void ExtendTimerByFactor(int maxDelayInBlockTimes)
            {
                var context = consensusService.context;
                TimeSpan nextDelay = consensusService.expected_delay - (TimeProvider.Current.UtcNow - consensusService.clock_started) + TimeSpan.FromMilliseconds(maxDelayInBlockTimes * Blockchain.MillisecondsPerBlock / context.M);
                if (!context.WatchOnly && !context.ViewChanging && !context.CommitSent && (nextDelay > TimeSpan.Zero))
                    ChangeTimer(nextDelay);
            }

            private bool AddTransaction(Transaction tx, bool verify)
            {
                var context = consensusService.context;
                if (verify && tx.Verify(context.Snapshot, context.SendersFeeMonitor.GetSenderFee(tx.Sender)) != RelayResultReason.Succeed)
                {
                    consensusService.Log($"Invalid transaction: {tx.Hash}{Environment.NewLine}{tx.ToArray().ToHexString()}", LogLevel.Warning);
                    RequestChangeView(ChangeViewReason.TxInvalid);
                    return false;
                }
                if (!NativeContract.Policy.CheckPolicy(tx, context.Snapshot))
                {
                    consensusService.Log($"reject tx: {tx.Hash}{Environment.NewLine}{tx.ToArray().ToHexString()}", LogLevel.Warning);
                    RequestChangeView(ChangeViewReason.TxRejectedByPolicy);
                    return false;
                }
                context.Transactions[tx.Hash] = tx;
                context.SendersFeeMonitor.AddSenderFee(tx);
                return CheckPrepareResponse();
            }

            private bool CheckPrepareResponse()
            {
                var context = consensusService.context;
                if (context.TransactionHashes.Length == context.Transactions.Count)
                {
                    // if we are the primary for this view, but acting as a backup because we recovered our own
                    // previously sent prepare request, then we don't want to send a prepare response.
                    if (context.IsPrimary || context.WatchOnly) return true;

                    // Check maximum block size via Native Contract policy
                    if (context.GetExpectedBlockSize() > NativeContract.Policy.GetMaxBlockSize(context.Snapshot))
                    {
                        consensusService.Log($"rejected block: {context.Block.Index}{Environment.NewLine} The size exceed the policy", LogLevel.Warning);
                        RequestChangeView(ChangeViewReason.BlockRejectedByPolicy);
                        return false;
                    }

                    // Timeout extension due to prepare response sent
                    // around 2*15/M=30.0/5 ~ 40% block time (for M=5)
                    ExtendTimerByFactor(2);

                    consensusService.Log($"send prepare response");
                    consensusService.localNode.Tell(new LocalNode.SendDirectly { Inventory = context.MakePrepareResponse() });
                    CheckPreparations();
                }
                return true;
            }

            private void ChangeTimer(TimeSpan delay)
            {
                consensusService.clock_started = TimeProvider.Current.UtcNow;
                consensusService.expected_delay = delay;
                consensusService.timer_token.CancelIfNotNull();
                consensusService.timer_token = Context.System.Scheduler.ScheduleTellOnceCancelable(delay, Self, new Timer
                {
                    Height = consensusService.context.Block.Index,
                    ViewNumber = consensusService.context.ViewNumber
                }, ActorRefs.NoSender);
            }

            private void CheckPreparations()
            {
                var context = consensusService.context;
                if (context.PreparationPayloads.Count(p => p != null) >= context.M && context.TransactionHashes.All(p => context.Transactions.ContainsKey(p)))
                {
                    ConsensusPayload payload = context.MakeCommit();
                    consensusService.Log($"send commit");
                    context.Save();
                    consensusService.localNode.Tell(new LocalNode.SendDirectly { Inventory = payload });
                    // Set timer, so we will resend the commit in case of a networking issue
                    ChangeTimer(TimeSpan.FromMilliseconds(Blockchain.MillisecondsPerBlock));
                    consensusService.CheckCommits();
                }
            }

            protected override void PostStop()
            {
                consensusService.Log("OnStop");
                consensusService.started = false;
                Context.System.EventStream.Unsubscribe(Self);
                consensusService.context.Dispose();
                base.PostStop();
            }

            private void SendPrepareRequest()
            {
                var context = consensusService.context;
                consensusService.Log($"send prepare request: height={context.Block.Index} view={context.ViewNumber}");
                consensusService.localNode.Tell(new LocalNode.SendDirectly { Inventory = context.MakePrepareRequest() });

                if (context.Validators.Length == 1)
                    CheckPreparations();

                if (context.TransactionHashes.Length > 0)
                {
                    foreach (InvPayload payload in InvPayload.CreateGroup(InventoryType.TX, context.TransactionHashes))
                        consensusService.localNode.Tell(Message.Create(MessageCommand.Inv, payload));
                }
                ChangeTimer(TimeSpan.FromMilliseconds((Blockchain.MillisecondsPerBlock << (context.ViewNumber + 1)) - (context.ViewNumber == 0 ? Blockchain.MillisecondsPerBlock : 0)));
            }
        }
    }
}
