﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;

#nullable disable

namespace Apex.Serialization.Internal
{
    [ExcludeFromCodeCoverage]
    internal static partial class HashHelpers
    {
        internal static readonly int[] DictionarySlimSizeOneIntArray = new int[1];

        internal static int PowerOf2(int v)
        {
            if ((v & (v - 1)) == 0) return v;
            int i = 2;
            while (i < v) i <<= 1;
            return i;
        }

        //// This is the maximum prime smaller than Array.MaxArrayLength
        //public const int MaxPrimeArrayLength = 0x7FEFFFFD;

        //public const int HashPrime = 101;

        //// Table of prime numbers to use as hash table sizes.
        //// A typical resize algorithm would pick the smallest prime number in this array
        //// that is larger than twice the previous capacity.
        //private static readonly int[] primes = {
        //    3, 7, 11, 17, 23, 29, 37, 47, 59, 71, 89, 107, 131, 163, 197, 239, 293, 353, 431, 521, 631, 761, 919,
        //    1103, 1327, 1597, 1931, 2333, 2801, 3371, 4049, 4861, 5839, 7013, 8419, 10103, 12143, 14591,
        //    17519, 21023, 25229, 30293, 36353, 43627, 52361, 62851, 75431, 90523, 108631, 130363, 156437,
        //    187751, 225307, 270371, 324449, 389357, 467237, 560689, 672827, 807403, 968897, 1162687, 1395263,
        //    1674319, 2009191, 2411033, 2893249, 3471899, 4166287, 4999559, 5999471, 7199369 };

        //public static bool IsPrime(int candidate)
        //{
        //    if ((candidate & 1) != 0)
        //    {
        //        int limit = (int)Math.Sqrt(candidate);
        //        for (int divisor = 3; divisor <= limit; divisor += 2)
        //        {
        //            if ((candidate % divisor) == 0)
        //                return false;
        //        }
        //        return true;
        //    }
        //    return (candidate == 2);
        //}

        //public static int GetPrime(int min)
        //{
        //    if (min < 0)
        //        throw new ArgumentException(Strings.Arg_HTCapacityOverflow);

        //    for (int i = 0; i < primes.Length; i++)
        //    {
        //        int prime = primes[i];
        //        if (prime >= min)
        //            return prime;
        //    }

        //    // outside of our predefined table.
        //    // compute the hard way.
        //    for (int i = (min | 1); i < int.MaxValue; i += 2)
        //    {
        //        if (IsPrime(i) && ((i - 1) % HashPrime != 0))
        //            return i;
        //    }
        //    return min;
        //}

        //// Returns size of hashtable to grow to.
        //public static int ExpandPrime(int oldSize)
        //{
        //    int newSize = 2 * oldSize;

        //    // Allow the hashtables to grow to maximum possible size (~2G elements) before encountering capacity overflow.
        //    // Note that this check works even when _items.Length overflowed thanks to the (uint) cast
        //    if ((uint)newSize > MaxPrimeArrayLength && MaxPrimeArrayLength > oldSize)
        //    {
        //        Debug.Assert(MaxPrimeArrayLength == GetPrime(MaxPrimeArrayLength), "Invalid MaxPrimeArrayLength");
        //        return MaxPrimeArrayLength;
        //    }

        //    return GetPrime(newSize);
        //}
    }

    /// <summary>
    /// DictionarySlim<TKey, TValue> is similar to Dictionary<TKey, TValue> but optimized in three ways:
    /// 1) It allows access to the value by ref replacing the common TryGetValue and Add pattern.
    /// 2) It does not store the hash code (assumes it is cheap to equate values).
    /// 3) It does not accept an equality comparer (assumes Object.GetHashCode() and Object.Equals() or overridden implementation are cheap and sufficient).
    /// </summary>
    [DebuggerTypeProxy(typeof(DictionarySlimDebugView<,>))]
    [DebuggerDisplay("Count = {Count}")]
    [ExcludeFromCodeCoverage]
    internal class DictionarySlim<TKey, TValue> : IReadOnlyCollection<KeyValuePair<TKey, TValue>>
    {
        // We want to initialize without allocating arrays. We also want to avoid null checks.
        // Array.Empty would give divide by zero in modulo operation. So we use static one element arrays.
        // The first add will cause a resize replacing these with real arrays of three elements.
        // Arrays are wrapped in a class to avoid being duplicated for each <TKey, TValue>
        private static readonly Entry[] InitialEntries = new Entry[1];
        private int _count;
        // 0-based index into _entries of head of free chain: -1 means empty
        private int _freeList = -1;
        // 1-based index into _entries; 0 means empty
        private int[] _buckets;
        private Entry[] _entries;


        [DebuggerDisplay("({key}, {value})->{next}")]
        private struct Entry
        {
            public TKey key;
            public TValue value;
            // 0-based index of next entry in chain: -1 means end of chain
            // also encodes whether this entry _itself_ is part of the free list by changing sign and subtracting 3,
            // so -2 means end of free list, -3 means index 0 but on free list, -4 means index 1 but on free list, etc.
            public int next;
            public int bucket;
        }

        public DictionarySlim()
        {
            _buckets = HashHelpers.DictionarySlimSizeOneIntArray;
            _entries = InitialEntries;
        }

        public DictionarySlim(int capacity)
        {
            if (capacity < 2)
                capacity = 2;
            capacity = HashHelpers.PowerOf2(capacity);
            _buckets = new int[capacity];
            _entries = new Entry[capacity];
        }

        public int Count => _count;

        public int Capacity => _entries.Length;

        public bool ContainsKey(TKey key)
        {
            Entry[] entries = _entries;
            for (int i = _buckets[key.GetHashCode() & (_buckets.Length - 1)] - 1;
                (uint)i < (uint)entries.Length; i = entries[i].next)
            {
                if (key.Equals(entries[i].key))
                    return true;
            }

            return false;
        }

        public bool TryGetValue(TKey key, out TValue value)
        {
            Entry[] entries = _entries;
            for (int i = _buckets[key.GetHashCode() & (_buckets.Length - 1)] - 1;
                (uint)i < (uint)entries.Length; i = entries[i].next)
            {
                if (key.Equals(entries[i].key))
                {
                    value = entries[i].value;
                    return true;
                }
            }

            value = default;
            return false;
        }

        /*
        public bool Remove(TKey key)
        {
            Entry[] entries = _entries;
            int bucketIndex = key.GetHashCode() & (_buckets.Length - 1);
            int entryIndex = _buckets[bucketIndex] - 1;

            int lastIndex = -1;
            while (entryIndex != -1)
            {
                Entry candidate = entries[entryIndex];
                if (candidate.key.Equals(key))
                {
                    if (lastIndex != -1)
                    {   // Fixup preceding element in chain to point to next (if any)
                        entries[lastIndex].next = candidate.next;
                    }
                    else
                    {   // Fixup bucket to new head (if any)
                        _buckets[bucketIndex] = candidate.next + 1;
                    }

                    entries[entryIndex] = default;

                    entries[entryIndex].next = -3 - _freeList; // New head of free list
                    _freeList = entryIndex;

                    _count--;
                    return true;
                }
                lastIndex = entryIndex;
                entryIndex = candidate.next;
            }

            return false;
        }
        */

        // Not safe for concurrent _reads_ (at least, if either of them add)
        public ref TValue GetOrAddValueRef(TKey key)
        {
            Entry[] entries = _entries;
            int bucketIndex = key.GetHashCode() & (_buckets.Length - 1);
            for (int i = _buckets[bucketIndex] - 1;
                (uint)i < (uint)entries.Length; i = entries[i].next)
            {
                if (key.Equals(entries[i].key))
                    return ref entries[i].value;
            }

            return ref AddKey(key, bucketIndex);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private ref TValue AddKey(TKey key, int bucketIndex)
        {
            Entry[] entries = _entries;
            int entryIndex;
            if (_freeList != -1)
            {
                entryIndex = _freeList;
                _freeList = -3 - entries[_freeList].next;
            }
            else
            {
                if (_count == entries.Length || entries.Length == 1)
                {
                    entries = Resize();
                    bucketIndex = key.GetHashCode() & (_buckets.Length - 1);
                    // entry indexes were not changed by Resize
                }
                entryIndex = _count;
            }

            entries[entryIndex].key = key;
            entries[entryIndex].next = _buckets[bucketIndex] - 1;
            entries[entryIndex].bucket = bucketIndex;
            _buckets[bucketIndex] = entryIndex + 1;
            _count++;
            return ref entries[entryIndex].value;
        }

        public unsafe void Clear()
        {
            if(_count == 0)
            {
                return;
            }

            if(_count < _buckets.Length >> 5)
            {
                for (int i = 0; i < _count; ++i)
                {
                    var bucket = _entries[i].bucket;
                    _entries[i] = default;
                    _buckets[bucket] = 0;
                }
            }
            else
            {
                fixed (void* b = &_buckets[0])
                {
                    Unsafe.InitBlock(b, 0, (uint)(4 * _buckets.Length));
                }

                Array.Clear(_entries, 0, _count);
            }

            _freeList = -1;
            _count = 0;
        }

        private Entry[] Resize()
        {
            int count = _count;
            int newSize = _entries.Length * 2;
            if ((uint)newSize > (uint)int.MaxValue) // uint cast handles overflow
                throw new InvalidOperationException("Strings.Arg_HTCapacityOverflow");

            var entries = new Entry[newSize];
            Array.Copy(_entries, 0, entries, 0, count);

            var newBuckets = new int[entries.Length];
            while (count-- > 0)
            {
                int bucketIndex = entries[count].key.GetHashCode() & (newBuckets.Length - 1);
                entries[count].bucket = bucketIndex;
                entries[count].next = newBuckets[bucketIndex] - 1;
                newBuckets[bucketIndex] = count + 1;
            }

            _buckets = newBuckets;
            _entries = entries;

            return entries;
        }

        public KeyCollection Keys => new KeyCollection(this);

        public ValueCollection Values => new ValueCollection(this);

        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int index)
        {
            if (array == null)
                throw new ArgumentNullException("array");
            // Let the runtime validate the index

            Entry[] entries = _entries;
            int i = 0;
            int count = _count;
            while (count > 0)
            {
                Entry entry = entries[i];
                if (entry.next > -2) // part of free list?
                {
                    count--;
                    array[index++] = new KeyValuePair<TKey, TValue>(
                        entry.key,
                        entry.value);
                }
                i++;
            }
        }

        public Enumerator GetEnumerator() => new Enumerator(this); // avoid boxing
        IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator() =>
            new Enumerator(this);
        IEnumerator IEnumerable.GetEnumerator() => new Enumerator(this);

        [ExcludeFromCodeCoverage]
        public struct Enumerator : IEnumerator<KeyValuePair<TKey, TValue>>
        {
            private readonly DictionarySlim<TKey, TValue> _dictionary;
            private int _index;
            private int _count;
            private KeyValuePair<TKey, TValue> _current;

            internal Enumerator(DictionarySlim<TKey, TValue> dictionary)
            {
                _dictionary = dictionary;
                _index = 0;
                _count = _dictionary._count;
                _current = default;
            }

            public bool MoveNext()
            {
                if (_count == 0)
                {
                    _current = default;
                    return false;
                }

                _count--;

                while (_dictionary._entries[_index].next < -1)
                    _index++;

                _current = new KeyValuePair<TKey, TValue>(
                    _dictionary._entries[_index].key,
                    _dictionary._entries[_index++].value);
                return true;
            }

            public KeyValuePair<TKey, TValue> Current => _current;

            object IEnumerator.Current => _current;

            void IEnumerator.Reset()
            {
                _index = 0;
                _count = _dictionary._count;
            }

            public void Dispose() { }
        }

        [ExcludeFromCodeCoverage]
        public struct KeyCollection : ICollection<TKey>, IReadOnlyCollection<TKey>
        {
            private readonly DictionarySlim<TKey, TValue> _dictionary;

            internal KeyCollection(DictionarySlim<TKey, TValue> dictionary)
            {
                _dictionary = dictionary;
            }

            public int Count => _dictionary._count;

            bool ICollection<TKey>.IsReadOnly => true;

            void ICollection<TKey>.Add(TKey item) =>
                throw new NotSupportedException();

            void ICollection<TKey>.Clear() =>
                throw new NotSupportedException();

            public bool Contains(TKey item) => _dictionary.ContainsKey(item);

            bool ICollection<TKey>.Remove(TKey item) =>
                throw new NotSupportedException();

            void ICollection<TKey>.CopyTo(TKey[] array, int index)
            {
                if (array == null)
                    throw new ArgumentNullException("array");
                // Let the runtime validate the index

                Entry[] entries = _dictionary._entries;
                int i = 0;
                int count = _dictionary._count;
                while (count > 0)
                {
                    Entry entry = entries[i];
                    if (entry.next > -2)  // part of free list?
                    {
                        array[index++] = entry.key;
                        count--;
                    }
                    i++;
                }
            }

            public Enumerator GetEnumerator() => new Enumerator(_dictionary); // avoid boxing
            IEnumerator<TKey> IEnumerable<TKey>.GetEnumerator() => new Enumerator(_dictionary);
            IEnumerator IEnumerable.GetEnumerator() => new Enumerator(_dictionary);

            [ExcludeFromCodeCoverage]
            public struct Enumerator : IEnumerator<TKey>
            {
                private readonly DictionarySlim<TKey, TValue> _dictionary;
                private int _index;
                private int _count;
                private TKey _current;

                internal Enumerator(DictionarySlim<TKey, TValue> dictionary)
                {
                    _dictionary = dictionary;
                    _index = 0;
                    _count = _dictionary._count;
                    _current = default;
                }

                public TKey Current => _current;

                object IEnumerator.Current => _current;

                public void Dispose() { }

                public bool MoveNext()
                {
                    if (_count == 0)
                    {
                        _current = default;
                        return false;
                    }

                    _count--;

                    while (_dictionary._entries[_index].next < -1)
                        _index++;

                    _current = _dictionary._entries[_index++].key;
                    return true;
                }

                void IEnumerator.Reset()
                {
                    _index = 0;
                    _count = _dictionary._count;
                }
            }
        }

        [ExcludeFromCodeCoverage]
        public struct ValueCollection : ICollection<TValue>, IReadOnlyCollection<TValue>
        {
            private readonly DictionarySlim<TKey, TValue> _dictionary;

            internal ValueCollection(DictionarySlim<TKey, TValue> dictionary)
            {
                _dictionary = dictionary;
            }

            public int Count => _dictionary._count;

            bool ICollection<TValue>.IsReadOnly => true;

            void ICollection<TValue>.Add(TValue item) =>
                throw new NotSupportedException();

            void ICollection<TValue>.Clear() =>
                throw new NotSupportedException();

            bool ICollection<TValue>.Contains(TValue item) =>
                throw new NotSupportedException();

            bool ICollection<TValue>.Remove(TValue item) =>
                throw new NotSupportedException();

            void ICollection<TValue>.CopyTo(TValue[] array, int index)
            {
                if (array == null)
                    throw new ArgumentNullException("array");
                // Let the runtime validate the index

                Entry[] entries = _dictionary._entries;
                int i = 0;
                int count = _dictionary._count;
                while (count > 0)
                {
                    if (entries[i].next > -2)  // part of free list?
                    {
                        array[index++] = entries[i].value;
                        count--;
                    }
                    i++;
                }
            }

            public Enumerator GetEnumerator() => new Enumerator(_dictionary); // avoid boxing
            IEnumerator<TValue> IEnumerable<TValue>.GetEnumerator() => new Enumerator(_dictionary);
            IEnumerator IEnumerable.GetEnumerator() => new Enumerator(_dictionary);

            [ExcludeFromCodeCoverage]
            public struct Enumerator : IEnumerator<TValue>
            {
                private readonly DictionarySlim<TKey, TValue> _dictionary;
                private int _index;
                private int _count;
                private TValue _current;

                internal Enumerator(DictionarySlim<TKey, TValue> dictionary)
                {
                    _dictionary = dictionary;
                    _index = 0;
                    _count = _dictionary._count;
                    _current = default;
                }

                public TValue Current => _current;

                object IEnumerator.Current => _current;

                public void Dispose() { }

                public bool MoveNext()
                {
                    if (_count == 0)
                    {
                        _current = default;
                        return false;
                    }

                    _count--;

                    while (_dictionary._entries[_index].next < -1)
                        _index++;

                    _current = _dictionary._entries[_index++].value;
                    return true;
                }

                void IEnumerator.Reset()
                {
                    _index = 0;
                    _count = _dictionary._count;
                }
            }
        }
    }

    [ExcludeFromCodeCoverage]
    internal sealed class DictionarySlimDebugView<K, V>
    {
        private readonly DictionarySlim<K, V> _dictionary;

        public DictionarySlimDebugView(DictionarySlim<K, V> dictionary)
        {
            _dictionary = dictionary ?? throw new ArgumentNullException(nameof(dictionary));
        }

        [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
        public KeyValuePair<K, V>[] Items
        {
            get
            {
                return _dictionary.ToArray();
            }
        }
    }
}
