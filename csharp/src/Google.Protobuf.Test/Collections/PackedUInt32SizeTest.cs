#region Copyright notice and license
// Protocol Buffers - Google's data interchange format
// Copyright 2015 Google Inc.  All rights reserved.
//
// Use of this source code is governed by a BSD-style
// license that can be found in the LICENSE file or at
// https://developers.google.com/open-source/licenses/bsd
#endregion

using System.Linq;
using Google.Protobuf.TestProtos;
using NUnit.Framework;

namespace Google.Protobuf.Collections
{
    /// <summary>
    /// Covers the bulk path used to total the encoded length of a packed uint32
    /// field. A wrong total is not a slow serialization but a corrupt one, because
    /// it becomes the length prefix, so these check the size against both the
    /// per-element calculator and the bytes actually produced.
    /// </summary>
    public class PackedUInt32SizeTest
    {
        /// <summary>
        /// Straddles every point at which the varint length changes, which is where
        /// a threshold comparison would be wrong if it used the wrong bound.
        /// </summary>
        private static readonly uint[] BoundaryValues =
        {
            0, 1, 127, 128, 16383, 16384, 2097151, 2097152,
            268435455, 268435456, uint.MaxValue - 1, uint.MaxValue
        };

        private static void AssertSizeIsConsistent(uint[] values)
        {
            var message = new TestAllTypes();
            message.RepeatedUint32.Add(values);

            int declared = message.CalculateSize();
            byte[] actual = message.ToByteArray();

            // CalculateSize is what allocates the buffer; if it disagrees with the
            // bytes written, serialization is broken rather than merely slow.
            Assert.AreEqual(actual.Length, declared);

            // And the values have to survive the round trip with that length prefix.
            var parsed = TestAllTypes.Parser.ParseFrom(actual);
            CollectionAssert.AreEqual(values, parsed.RepeatedUint32);
        }

        [Test]
        public void SizeMatchesAtVarintLengthBoundaries()
        {
            AssertSizeIsConsistent(BoundaryValues);
        }

        [Test]
        public void SizeMatchesForEmptyField()
        {
            AssertSizeIsConsistent(new uint[0]);
        }

        [Test]
        public void SizeMatchesForSingleElement()
        {
            AssertSizeIsConsistent(new uint[] { 300 });
        }

        [Test]
        public void SizeMatchesForLongRun()
        {
            // Long enough to clear the vectorised loop and leave a scalar tail.
            var values = Enumerable.Range(0, 1000).Select(i => (uint) (i * 7919)).ToArray();
            AssertSizeIsConsistent(values);
        }

        /// <summary>
        /// Lengths either side of a vector width, so the boundary between the
        /// vectorised body and the scalar tail is exercised in both directions.
        /// </summary>
        [Test]
        public void SizeMatchesAroundVectorWidth()
        {
            for (int n = 0; n <= 40; n++)
            {
                var values = Enumerable.Range(0, n).Select(i => (uint) (i * 65599)).ToArray();
                AssertSizeIsConsistent(values);
            }
        }

        /// <summary>
        /// The total must equal the sum of the per-element calculator the general
        /// path uses, element for element.
        /// </summary>
        [Test]
        public void SizeAgreesWithPerElementCalculator()
        {
            var field = new RepeatedField<uint>();
            field.Add(BoundaryValues);

            uint tag = WireFormat.MakeTag(1, WireFormat.WireType.LengthDelimited);
            var codec = FieldCodec.ForUInt32(tag);

            int expectedData = BoundaryValues.Sum(v => CodedOutputStream.ComputeUInt32Size(v));
            int expected = CodedOutputStream.ComputeRawVarint32Size(codec.Tag)
                           + CodedOutputStream.ComputeLengthSize(expectedData)
                           + expectedData;

            Assert.AreEqual(expected, field.CalculateSize(codec));
        }

        /// <summary>
        /// fixed32 is also a FieldCodec&lt;uint&gt;; it must keep its own O(1) sizing
        /// rather than being treated as a varint.
        /// </summary>
        [Test]
        public void Fixed32SizeIsUnaffected()
        {
            var message = new TestAllTypes();
            message.RepeatedFixed32.Add(BoundaryValues);

            Assert.AreEqual(message.ToByteArray().Length, message.CalculateSize());
        }
    }
}
