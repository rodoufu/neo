using Microsoft.EntityFrameworkCore;
using Neo.Cryptography;
using Neo.IO;
using Neo.SmartContract;
using Neo.Wallets.NEP6;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace Neo.Wallets.SQLite
{
    public class UserWallet : Wallet
    {
        private readonly object db_lock = new object();
        private readonly string path;
        private readonly byte[] iv;
        private readonly byte[] salt;
        private readonly byte[] masterKey;
        private readonly ScryptParameters scrypt;
        private readonly Dictionary<UInt160, UserWalletAccount> accounts;

        public override string Name => Path.GetFileNameWithoutExtension(path);

        public override Version Version
        {
            get
            {
                byte[] buffer = LoadStoredData("Version");
                if (buffer == null || buffer.Length < 16) return new Version(0, 0);
                int major = BinaryPrimitives.ReadInt32LittleEndian(buffer);
                int minor = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(4));
                int build = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(8));
                int revision = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(12));
                return new Version(major, minor, build, revision);
            }
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="path">Path</param>
        /// <param name="passwordKey">Password Key</param>
        /// <param name="create">True for create the wallet</param>
        /// <param name="scrypt">Scrypt initialization value (only if create=True)</param>
        private UserWallet(string path, byte[] passwordKey, bool create, ScryptParameters scrypt = null)
        {
            this.path = path;

            if (create)
            {
                this.iv = new byte[16];
                this.salt = new byte[20];
                this.masterKey = new byte[32];
                this.scrypt = scrypt ?? ScryptParameters.Default;
                this.accounts = new Dictionary<UInt160, UserWalletAccount>();
                using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
                {
                    rng.GetBytes(iv);
                    rng.GetBytes(salt);
                    rng.GetBytes(masterKey);
                }
                Version version = Assembly.GetExecutingAssembly().GetName().Version;
                BuildDatabase();
                SaveStoredData("IV", iv);
                SaveStoredData("Salt", salt);
                SaveStoredData("PasswordHash", passwordKey.Concat(salt).ToArray().Sha256());
                SaveStoredData("MasterKey", masterKey.AesEncrypt(passwordKey, iv));
                SaveStoredData("Version", new[] { version.Major, version.Minor, version.Build, version.Revision }.Select(p => BitConverter.GetBytes(p)).SelectMany(p => p).ToArray());
                SaveStoredData("ScryptN", BitConverter.GetBytes(this.scrypt.N));
                SaveStoredData("ScryptR", BitConverter.GetBytes(this.scrypt.R));
                SaveStoredData("ScryptP", BitConverter.GetBytes(this.scrypt.P));
            }
            else
            {
                this.salt = LoadStoredData("Salt");
                byte[] passwordHash = LoadStoredData("PasswordHash");
                if (passwordHash != null && !passwordHash.SequenceEqual(passwordKey.Concat(salt).ToArray().Sha256()))
                    throw new CryptographicException();
                this.iv = LoadStoredData("IV");
                this.masterKey = LoadStoredData("MasterKey").AesDecrypt(passwordKey, iv);
                this.scrypt = new ScryptParameters
                    (
                    BitConverter.ToInt32(LoadStoredData("ScryptN")),
                    BitConverter.ToInt32(LoadStoredData("ScryptR")),
                    BitConverter.ToInt32(LoadStoredData("ScryptP"))
                    );
                this.accounts = LoadAccounts();
            }
        }

        private void AddAccount(UserWalletAccount account)
        {
            lock (accounts)
            {
                if (accounts.TryGetValue(account.ScriptHash, out UserWalletAccount account_old))
                {
                    if (account.Contract == null)
                    {
                        account.Contract = account_old.Contract;
                    }
                }
                accounts[account.ScriptHash] = account;
            }
            lock (db_lock)
                using (WalletDataContext ctx = new WalletDataContext(path))
                {
                    if (account.HasKey)
                    {
                        string passphrase = Encoding.UTF8.GetString(masterKey);
                        Account db_account = ctx.Accounts.FirstOrDefault(p => p.PublicKeyHash == account.Key.PublicKeyHash.ToArray());
                        if (db_account == null)
                        {
                            db_account = ctx.Accounts.Add(new Account
                            {
                                Nep2key = account.Key.Export(passphrase, scrypt.N, scrypt.R, scrypt.P),
                                PublicKeyHash = account.Key.PublicKeyHash.ToArray()
                            }).Entity;
                        }
                        else
                        {
                            db_account.Nep2key = account.Key.Export(passphrase, scrypt.N, scrypt.R, scrypt.P);
                        }
                    }
                    if (account.Contract != null)
                    {
                        Contract db_contract = ctx.Contracts.FirstOrDefault(p => p.ScriptHash == account.Contract.ScriptHash.ToArray());
                        if (db_contract != null)
                        {
                            db_contract.PublicKeyHash = account.Key.PublicKeyHash.ToArray();
                        }
                        else
                        {
                            ctx.Contracts.Add(new Contract
                            {
                                RawData = ((VerificationContract)account.Contract).ToArray(),
                                ScriptHash = account.Contract.ScriptHash.ToArray(),
                                PublicKeyHash = account.Key.PublicKeyHash.ToArray()
                            });
                        }
                    }
                    //add address
                    {
                        Address db_address = ctx.Addresses.FirstOrDefault(p => p.ScriptHash == account.ScriptHash.ToArray());
                        if (db_address == null)
                        {
                            ctx.Addresses.Add(new Address
                            {
                                ScriptHash = account.ScriptHash.ToArray()
                            });
                        }
                    }
                    ctx.SaveChanges();
                }
        }

        private void BuildDatabase()
        {
            using (WalletDataContext ctx = new WalletDataContext(path))
            {
                ctx.Database.EnsureDeleted();
                ctx.Database.EnsureCreated();
            }
        }

        public bool ChangePassword(string password_old, string password_new)
        {
            if (!VerifyPassword(password_old)) return false;
            byte[] passwordKey = password_new.ToAesKey();
            try
            {
                SaveStoredData("PasswordHash", passwordKey.Sha256());
                SaveStoredData("MasterKey", masterKey.AesEncrypt(passwordKey, iv));
                return true;
            }
            finally
            {
                Array.Clear(passwordKey, 0, passwordKey.Length);
            }
        }

        public override bool Contains(UInt160 scriptHash)
        {
            lock (accounts)
            {
                return accounts.ContainsKey(scriptHash);
            }
        }

        public static UserWallet Create(string path, string password)
        {
            return new UserWallet(path, password.ToAesKey(), true);
        }

        public static UserWallet Create(string path, SecureString password)
        {
            return new UserWallet(path, password.ToAesKey(), true);
        }

        public override WalletAccount CreateAccount(byte[] privateKey)
        {
            KeyPair key = new KeyPair(privateKey);
            VerificationContract contract = new VerificationContract
            {
                Script = SmartContract.Contract.CreateSignatureRedeemScript(key.PublicKey),
                ParameterList = new[] { ContractParameterType.Signature }
            };
            UserWalletAccount account = new UserWalletAccount(contract.ScriptHash)
            {
                Key = key,
                Contract = contract
            };
            AddAccount(account);
            return account;
        }

        public override WalletAccount CreateAccount(SmartContract.Contract contract, KeyPair key = null)
        {
            VerificationContract verification_contract = contract as VerificationContract;
            if (verification_contract == null)
            {
                verification_contract = new VerificationContract
                {
                    Script = contract.Script,
                    ParameterList = contract.ParameterList
                };
            }
            UserWalletAccount account = new UserWalletAccount(verification_contract.ScriptHash)
            {
                Key = key,
                Contract = verification_contract
            };
            AddAccount(account);
            return account;
        }

        public override WalletAccount CreateAccount(UInt160 scriptHash)
        {
            UserWalletAccount account = new UserWalletAccount(scriptHash);
            AddAccount(account);
            return account;
        }

        public override bool DeleteAccount(UInt160 scriptHash)
        {
            UserWalletAccount account;
            lock (accounts)
            {
                if (accounts.TryGetValue(scriptHash, out account))
                    accounts.Remove(scriptHash);
            }
            if (account != null)
            {
                lock (db_lock)
                    using (WalletDataContext ctx = new WalletDataContext(path))
                    {
                        if (account.HasKey)
                        {
                            Account db_account = ctx.Accounts.First(p => p.PublicKeyHash == account.Key.PublicKeyHash.ToArray());
                            ctx.Accounts.Remove(db_account);
                        }
                        if (account.Contract != null)
                        {
                            Contract db_contract = ctx.Contracts.First(p => p.ScriptHash == scriptHash.ToArray());
                            ctx.Contracts.Remove(db_contract);
                        }
                        //delete address
                        {
                            Address db_address = ctx.Addresses.First(p => p.ScriptHash == scriptHash.ToArray());
                            ctx.Addresses.Remove(db_address);
                        }
                        ctx.SaveChanges();
                    }
                return true;
            }
            return false;
        }

        public override WalletAccount GetAccount(UInt160 scriptHash)
        {
            lock (accounts)
            {
                accounts.TryGetValue(scriptHash, out UserWalletAccount account);
                return account;
            }
        }

        public override IEnumerable<WalletAccount> GetAccounts()
        {
            lock (accounts)
            {
                foreach (UserWalletAccount account in accounts.Values)
                    yield return account;
            }
        }

        private Dictionary<UInt160, UserWalletAccount> LoadAccounts()
        {
            using (WalletDataContext ctx = new WalletDataContext(path))
            {
                string passphrase = Encoding.UTF8.GetString(masterKey);
                Dictionary<UInt160, UserWalletAccount> accounts = ctx.Addresses.Select(p => p.ScriptHash).AsEnumerable().Select(p => new UserWalletAccount(new UInt160(p))).ToDictionary(p => p.ScriptHash);
                foreach (Contract db_contract in ctx.Contracts.Include(p => p.Account))
                {
                    VerificationContract contract = db_contract.RawData.AsSerializable<VerificationContract>();
                    UserWalletAccount account = accounts[contract.ScriptHash];
                    account.Contract = contract;
                    account.Key = new KeyPair(GetPrivateKeyFromNEP2(db_contract.Account.Nep2key, passphrase, scrypt.N, scrypt.R, scrypt.P));
                }
                return accounts;
            }
        }

        private byte[] LoadStoredData(string name)
        {
            using (WalletDataContext ctx = new WalletDataContext(path))
            {
                return ctx.Keys.FirstOrDefault(p => p.Name == name)?.Value;
            }
        }

        public static UserWallet Open(string path, string password)
        {
            return new UserWallet(path, password.ToAesKey(), false);
        }

        public static UserWallet Open(string path, SecureString password)
        {
            return new UserWallet(path, password.ToAesKey(), false);
        }

        private void SaveStoredData(string name, byte[] value)
        {
            lock (db_lock)
                using (WalletDataContext ctx = new WalletDataContext(path))
                {
                    SaveStoredData(ctx, name, value);
                    ctx.SaveChanges();
                }
        }

        private static void SaveStoredData(WalletDataContext ctx, string name, byte[] value)
        {
            Key key = ctx.Keys.FirstOrDefault(p => p.Name == name);
            if (key == null)
            {
                ctx.Keys.Add(new Key
                {
                    Name = name,
                    Value = value
                });
            }
            else
            {
                key.Value = value;
            }
        }

        public override bool VerifyPassword(string password)
        {
            return password.ToAesKey().Concat(salt).ToArray().Sha256().SequenceEqual(LoadStoredData("PasswordHash"));
        }
    }
}
