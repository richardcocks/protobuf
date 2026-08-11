#region Copyright notice and license
// Protocol Buffers - Google's data interchange format
// Copyright 2015 Google Inc.  All rights reserved.
//
// Use of this source code is governed by a BSD-style
// license that can be found in the LICENSE file or at
// https://developers.google.com/open-source/licenses/bsd
#endregion

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Google.Protobuf.TestProtos;
using NUnit.Framework;

namespace Google.Protobuf.Collections
{
    /// <summary>
    /// Covers the bulk path RepeatedField takes for a packed uint32 field whose
    /// payload is wholly buffered. It must agree with the general element-at-a-time
    /// loop in every case, including inputs it is required to decline.
    /// </summary>
    /// <remarks>
    /// Round trips go through TestPackedTypes, not TestAllTypes: unittest.proto is
    /// proto2, where a repeated field is not packed unless declared so, and
    /// serializing TestAllTypes.RepeatedUint32 produces a tag per element rather than
    /// a length-delimited run. Such input never reaches the code under test. The tests
    /// that build a tag by hand do use TestAllTypes, deliberately: a parser must accept
    /// a packed encoding for any repeated scalar however the field was declared, and
    /// that is worth covering as well as being a route into the bulk path.
    /// </remarks>
    public class PackedUInt32ParseTest
    {
        /// <summary>
        /// Straddles each point where the varint length changes, so an off-by-one in
        /// the element count or in the shift sequence shows up as a wrong value or a
        /// wrong element count rather than passing by luck.
        /// </summary>
        private static readonly uint[] BoundaryValues =
        {
            0, 1, 127, 128, 16383, 16384, 2097151, 2097152,
            268435455, 268435456, uint.MaxValue - 1, uint.MaxValue
        };

        private static byte[] SerializeUInt32s(IEnumerable<uint> values)
        {
            var message = new TestPackedTypes();
            message.PackedUint32.Add(values);
            return message.ToByteArray();
        }

        /// <summary>
        /// Parsing from a stream refills a buffer as it goes, so the payload is not
        /// guaranteed to be contiguous and the bulk path must decline. Both routes
        /// have to produce the same values.
        /// </summary>
        private static void AssertBothPathsAgree(uint[] expected)
        {
            byte[] bytes = SerializeUInt32s(expected);

            var fromArray = TestPackedTypes.Parser.ParseFrom(bytes);
            var coded = new CodedInputStream(new MemoryStream(bytes));
            var fromStream = TestPackedTypes.Parser.ParseFrom(coded);

            CollectionAssert.AreEqual(expected, fromArray.PackedUint32);
            CollectionAssert.AreEqual(expected, fromStream.PackedUint32);

            // A sequence splits the payload across buffers, so the run is not
            // contiguous and the bulk path has to decline. Several segment sizes,
            // because the interesting case is a run that straddles a boundary.
            foreach (int segmentSize in new[] { 1, 2, 3, 7, 16, 64 })
            {
                var sequence = ReadOnlySequenceFactory.CreateWithContent(bytes, segmentSize);
                var fromSequence = TestPackedTypes.Parser.ParseFrom(sequence);
                CollectionAssert.AreEqual(expected, fromSequence.PackedUint32,
                    $"segment size {segmentSize}");
            }
        }

        [Test]
        public void RoundTripsVarintLengthBoundaries()
        {
            AssertBothPathsAgree(BoundaryValues);
        }

        [Test]
        public void RoundTripsEmptyField()
        {
            var message = TestPackedTypes.Parser.ParseFrom(SerializeUInt32s(new uint[0]));
            Assert.AreEqual(0, message.PackedUint32.Count);
        }

        [Test]
        public void RoundTripsSingleElement()
        {
            AssertBothPathsAgree(new uint[] { 300 });
        }

        [Test]
        public void RoundTripsLongRun()
        {
            // Long enough that the count and the decode disagree if either drifts.
            var values = Enumerable.Range(0, 1000).Select(i => (uint) (i * 7919)).ToArray();
            AssertBothPathsAgree(values);
        }

        [Test]
        public void AppendsToExistingContents()
        {
            // Two packed runs for the same field concatenate rather than replace, so
            // the second must size the array from the existing count, not from zero.
            byte[] first = SerializeUInt32s(new uint[] { 1, 2, 3 });
            byte[] second = SerializeUInt32s(new uint[] { 4, 5, 6 });
            byte[] both = first.Concat(second).ToArray();

            var message = TestPackedTypes.Parser.ParseFrom(both);
            CollectionAssert.AreEqual(new uint[] { 1, 2, 3, 4, 5, 6 }, message.PackedUint32);
        }

        /// <summary>
        /// A negative int32 written to a uint32 field is sign-extended to ten bytes.
        /// It is valid on the wire and must decode to the truncated 32-bit value, so
        /// counting terminator bytes still has to yield one element here, not two.
        /// </summary>
        [Test]
        public void AcceptsOverlongEncoding()
        {
            byte[] fiveByte = { 0xFF, 0xFF, 0xFF, 0xFF, 0x0F };
            // -1024 sign-extended to 64 bits, seven bits at a time, least significant
            // group first: 0x00, 0x78, then 0x7F until the final group.
            byte[] tenByte = { 0x80, 0xF8, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01 };

            var stream = new MemoryStream();
            var output = new CodedOutputStream(stream);
            output.WriteTag(
                TestAllTypes.RepeatedUint32FieldNumber, WireFormat.WireType.LengthDelimited);
            output.WriteLength(fiveByte.Length + tenByte.Length);
            output.WriteRawBytes(fiveByte);
            output.WriteRawBytes(tenByte);
            output.Flush();
            byte[] bytes = stream.ToArray();

            var fromArray = TestAllTypes.Parser.ParseFrom(bytes);
            var coded = new CodedInputStream(new MemoryStream(bytes));
            var fromStream = TestAllTypes.Parser.ParseFrom(coded);

            Assert.AreEqual(2, fromArray.RepeatedUint32.Count);
            Assert.AreEqual(uint.MaxValue, fromArray.RepeatedUint32[0]);
            Assert.AreEqual(unchecked((uint) -1024), fromArray.RepeatedUint32[1]);

            // The bulk path and the general loop must not diverge here.
            CollectionAssert.AreEqual(fromArray.RepeatedUint32, fromStream.RepeatedUint32);
        }

        /// <summary>
        /// A truncated final varint must not be read past the end of the run. The
        /// bulk path declines this and the general loop reports the error.
        /// </summary>
        [Test]
        public void RejectsTruncatedFinalVarint()
        {
            var stream = new MemoryStream();
            var output = new CodedOutputStream(stream);
            output.WriteTag(
                TestAllTypes.RepeatedUint32FieldNumber, WireFormat.WireType.LengthDelimited);
            output.WriteLength(2);
            output.WriteRawBytes(new byte[] { 0x01, 0x80 }); // second varint never terminates
            output.Flush();

            Assert.Throws<InvalidProtocolBufferException>(
                () => TestAllTypes.Parser.ParseFrom(stream.ToArray()));
        }

        /// <summary>
        /// fixed32 is also a FieldCodec&lt;uint&gt;, so it must not be mistaken for a
        /// varint codec -- that would reinterpret four raw bytes as varints.
        /// </summary>
        [Test]
        public void Fixed32FieldIsUnaffected()
        {
            var message = new TestPackedTypes();
            message.PackedFixed32.Add(BoundaryValues);

            var parsed = TestPackedTypes.Parser.ParseFrom(message.ToByteArray());

            CollectionAssert.AreEqual(BoundaryValues, parsed.PackedFixed32);
        }
    }
}
