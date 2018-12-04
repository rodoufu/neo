﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Neo.IO;

namespace Neo.Ledger
{
    /// <summary>
    /// Modified Merkel Patricia Node.
    /// Note: It is not a thread safe implementation.
    /// </summary>
    public class MerklePatriciaNode : StateBase, ICloneable<MerklePatriciaNode>, IEquatable<MerklePatriciaNode>
    {
        private const int BranchSize = 18;
        private const int ExtensionSize = 2;
        private const int LeafSize = 3;

        private byte[][] _hashes;
        private MerklePatriciaNode(int size = 0) => _hashes = new byte[size][];

        /// <summary>
        /// Indicates if the node is a branch.
        /// </summary>
        public bool IsBranch => _hashes.Length == BranchSize;

        /// <summary>
        /// Indicates if the node is an extension.
        /// </summary>
        public bool IsExtension => _hashes.Length == ExtensionSize;

        /// <summary>
        /// Indicates if the node is a leaf.
        /// </summary>
        public bool IsLeaf => _hashes.Length == LeafSize;

        /// <summary>
        /// Get and set the hashes by index.
        /// </summary>
        /// <param name="index">Index of the hash to get or set.</param>
        public byte[] this[int index]
        {
            get => _hashes[index];
            set => _hashes[index] = value;
        }

        /// <summary>
        /// Get and set the path of the node.
        /// Used for leaf and extension nodes.
        /// </summary>
        public byte[] Path
        {
            get => _hashes[0];
            set => _hashes[0] = value;
        }

        /// <summary>
        /// Get and set the key of the node.
        /// Used for leaf and branch nodes.
        /// </summary>
        public byte[] Key
        {
            get => _hashes[_hashes.Length - 2];
            set => _hashes[_hashes.Length - 2] = value;
        }

        /// <summary>
        /// Get and set the value of the node.
        /// Used for leaf and branch nodes.
        /// </summary>
        public byte[] Value
        {
            get => _hashes[_hashes.Length - 1];
            set => _hashes[_hashes.Length - 1] = value;
        }

        /// <summary>
        /// Get and set the hash of the next node.
        /// Only for extension node.
        /// </summary>
        public byte[] Next
        {
            get => _hashes[_hashes.Length - 1];
            set => _hashes[_hashes.Length - 1] = value;
        }

        /// <summary>
        /// Calculates the node hash.
        /// </summary>
        /// <returns>The calculated hash.</returns>
        public byte[] Hash()
        {
            var bytes = new List<byte>();
            for (var i = 0; i < _hashes.Length; i++)
            {
                bytes.Add((byte) i);
                if (_hashes[i] != null)
                {
                    bytes.AddRange(_hashes[i]);
                }
            }

            return new System.Security.Cryptography.SHA256Managed().ComputeHash(bytes.ToArray());
        }

        /// <inheritdoc />
        public override string ToString()
        {
            var resp = new StringBuilder(IsBranch ? "{" : "[");
            var virgula = false;
            for (var i = 0; i < _hashes.Length; i++)
            {
                if (IsBranch && _hashes[i] == null) continue;
                resp.Append(virgula ? "," : "")
                    .Append(IsBranch ? $"\"{i:x}\":" : "")
                    .Append(_hashes[i] != null ? $"\"{_hashes[i].ByteToHexString(false, false)}\"" : "null");
                virgula = true;
            }

            return resp.Append(IsBranch ? "}" : "]").ToString();
        }

        /// <summary>
        /// The number of hashes on the node.
        /// </summary>
        public int Length => _hashes.Length;

        /// <summary>
        /// Creates a branch node.
        /// </summary>
        /// <returns>The node created.</returns>
        public static MerklePatriciaNode BranchNode() => new MerklePatriciaNode(BranchSize);

        /// <summary>
        /// Creates an extension node.
        /// </summary>
        /// <returns>The node created.</returns>
        public static MerklePatriciaNode ExtensionNode() => new MerklePatriciaNode(ExtensionSize);

        /// <summary>
        /// Creates a leaf node.
        /// </summary>
        /// <returns>The node created.</returns>
        public static MerklePatriciaNode LeafNode() => new MerklePatriciaNode(LeafSize);

        /// <inheritdoc />
        public MerklePatriciaNode Clone()
        {
            var resp = new MerklePatriciaNode(Length);
            for (var i = 0; i < Length; i++)
            {
                resp._hashes[i] = _hashes[i] != null ? _hashes[i].ToArray() : null;
            }

            return resp;
        }

        /// <inheritdoc />
        public void FromReplica(MerklePatriciaNode replica)
        {
            _hashes = new byte[replica.Length][];
            for (var i = 0; i < Length; i++)
            {
                _hashes[i] = replica._hashes[i] != null ? replica._hashes[i].ToArray() : null;
            }
        }

        /// <inheritdoc />
        public bool Equals(MerklePatriciaNode other)
        {
            if (ReferenceEquals(null, other))
            {
                return false;
            }

            if (ReferenceEquals(this, other))
            {
                return true;
            }

            if (_hashes.Length != other.Length)
            {
                return false;
            }

            return !_hashes.Where((t, i) =>
                (t != null || other._hashes[i] != null) &&
                ((t == null && other._hashes[i] != null) || (t != null && other._hashes[i] == null) ||
                 !t.SequenceEqual(other._hashes[i]))).Any();
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            return obj.GetType() == GetType() && Equals((MerklePatriciaNode) obj);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            var resp = _hashes.Length;
            for (var i = 0; i < _hashes.Length; i++)
            {
                if (_hashes[i] == null) continue;
                resp += _hashes[i].Sum(b => i * _hashes.Length + b);
            }

            return resp;
        }

        /// <inheritdoc />
        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);
            _hashes = new byte[reader.ReadByte()][];
            for (var i = 0; i < _hashes.Length; i++)
            {
                _hashes[i] = reader.ReadVarBytes();
                _hashes[i] = IsBranch && _hashes[i].Length == 0 ? null : _hashes[i];
            }
        }

        /// <inheritdoc />
        public override void Serialize(BinaryWriter writer)
        {
            base.Serialize(writer);
            writer.Write((byte) _hashes.Length);
            foreach (var hash in _hashes)
            {
                writer.WriteVarBytes(hash ?? new byte[0]);
            }
        }
    }
}