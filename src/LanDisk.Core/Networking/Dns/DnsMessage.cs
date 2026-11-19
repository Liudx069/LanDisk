using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace LanDisk.Core.Networking.Dns;

public enum DnsRecordType : ushort
{
    A = 1,
    PTR = 12,
    TXT = 16,
    AAAA = 28,
    SRV = 33,
    NSEC = 47,
    ANY = 255
}

public sealed record DnsQuestion(string Name, DnsRecordType Type, bool UnicastResponse)
{
    public ushort Class => 1; // IN
}

/// <summary>
/// A resource record in wire form. <see cref="RData"/> is authoritative; <see cref="Target"/> is the
/// decoded name for the record types whose payload is a name (PTR/SRV), kept so callers do not have to
/// re-parse the payload.
/// </summary>
public sealed class DnsResource
{
    public required string Name { get; init; }
    public required DnsRecordType Type { get; init; }
    public ushort Class { get; init; } = 1;
    public uint Ttl { get; init; } = 120;
    public byte[] RData { get; init; } = [];
    public string? Target { get; init; }

    public string? AddressText
    {
        get
        {
            if (Type == DnsRecordType.A && RData.Length == 4) return new IPAddress(RData).ToString();
            if (Type == DnsRecordType.AAAA && RData.Length == 16) return new IPAddress(RData).ToString();
            return null;
        }
    }
}

/// <summary>
/// A minimal DNS message codec, sufficient for the records an mDNS responder has to emit and parse.
///
/// <para>Reading follows compression pointers, because every real mDNS client uses them and a
/// responder that cannot read them cannot detect a name conflict. Writing never compresses: the
/// messages here carry a handful of short records, and an uncompressed answer is unambiguously valid
/// where a hand-rolled compression table is a good place to hide a bug.</para>
/// </summary>
public sealed class DnsMessage
{
    public const int MaxNameDepth = 16;

    public ushort Id { get; set; }
    public bool IsResponse { get; set; }
    public int Opcode { get; set; }
    public bool Authoritative { get; set; }
    public bool Truncated { get; set; }
    public bool RecursionDesired { get; set; }
    public bool RecursionAvailable { get; set; }
    public int ResponseCode { get; set; }

    public List<DnsQuestion> Questions { get; } = [];
    public List<DnsResource> Answers { get; } = [];
    public List<DnsResource> Authorities { get; } = [];
    public List<DnsResource> Additionals { get; } = [];

    public bool IsQuery => !IsResponse;

    // ------------------------------------------------------------------ write

    public byte[] ToBytes()
    {
        using var stream = new MemoryStream();
        Span<byte> header = stackalloc byte[12];

        ushort flags = 0;
        if (IsResponse) flags |= 0x8000;
        flags |= (ushort)((Opcode & 0xF) << 11);
        if (Authoritative) flags |= 0x0400;
        if (Truncated) flags |= 0x0200;
        if (RecursionDesired) flags |= 0x0100;
        if (RecursionAvailable) flags |= 0x0080;
        flags |= (ushort)(ResponseCode & 0xF);

        BinaryPrimitives.WriteUInt16BigEndian(header[..2], Id);
        BinaryPrimitives.WriteUInt16BigEndian(header[2..4], flags);
        BinaryPrimitives.WriteUInt16BigEndian(header[4..6], (ushort)Questions.Count);
        BinaryPrimitives.WriteUInt16BigEndian(header[6..8], (ushort)Answers.Count);
        BinaryPrimitives.WriteUInt16BigEndian(header[8..10], (ushort)Authorities.Count);
        BinaryPrimitives.WriteUInt16BigEndian(header[10..12], (ushort)Additionals.Count);
        stream.Write(header);

        Span<byte> questionTail = stackalloc byte[4];
        foreach (DnsQuestion question in Questions)
        {
            WriteName(stream, question.Name);
            BinaryPrimitives.WriteUInt16BigEndian(questionTail[..2], (ushort)question.Type);
            BinaryPrimitives.WriteUInt16BigEndian(questionTail[2..4], question.UnicastResponse ? (ushort)0x8001 : (ushort)1);
            stream.Write(questionTail);
        }

        foreach (DnsResource record in Answers) WriteResource(stream, record);
        foreach (DnsResource record in Authorities) WriteResource(stream, record);
        foreach (DnsResource record in Additionals) WriteResource(stream, record);

        return stream.ToArray();
    }

    private static void WriteResource(Stream stream, DnsResource record)
    {
        WriteName(stream, record.Name);

        Span<byte> fixedPart = stackalloc byte[10];
        BinaryPrimitives.WriteUInt16BigEndian(fixedPart[..2], (ushort)record.Type);
        BinaryPrimitives.WriteUInt16BigEndian(fixedPart[2..4], record.Class);
        BinaryPrimitives.WriteUInt32BigEndian(fixedPart[4..8], record.Ttl);
        BinaryPrimitives.WriteUInt16BigEndian(fixedPart[8..10], (ushort)record.RData.Length);
        stream.Write(fixedPart);
        stream.Write(record.RData);
    }

    public static void WriteName(Stream stream, string name)
    {
        foreach (string label in SplitLabels(name))
        {
            byte[] bytes = Encoding.ASCII.GetBytes(label);
            if (bytes.Length is 0 or > 63)
                throw new InvalidOperationException($"DNS 标签长度非法：'{label}'");

            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes);
        }
        stream.WriteByte(0);
    }

    public static IEnumerable<string> SplitLabels(string name)
        => name.TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries);

    // ------------------------------------------------------------------ read

    public static DnsMessage Parse(byte[] buffer, int length)
    {
        if (length < 12) throw new InvalidDataException("DNS 报文过短。");

        var message = new DnsMessage
        {
            Id = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(0, 2))
        };

        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(2, 2));
        message.IsResponse = (flags & 0x8000) != 0;
        message.Opcode = (flags >> 11) & 0xF;
        message.Authoritative = (flags & 0x0400) != 0;
        message.Truncated = (flags & 0x0200) != 0;
        message.RecursionDesired = (flags & 0x0100) != 0;
        message.RecursionAvailable = (flags & 0x0080) != 0;
        message.ResponseCode = flags & 0xF;

        int questionCount = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(4, 2));
        int answerCount = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(6, 2));
        int authorityCount = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(8, 2));
        int additionalCount = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(10, 2));

        int offset = 12;
        for (int i = 0; i < questionCount; i++)
        {
            string name = ReadName(buffer, length, ref offset);
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(offset, 2));
            ushort qclass = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(offset + 2, 2));
            offset += 4;

            message.Questions.Add(new DnsQuestion(name, (DnsRecordType)type, (qclass & 0x8000) != 0));
        }

        ReadRecords(buffer, length, ref offset, answerCount, message.Answers);
        ReadRecords(buffer, length, ref offset, authorityCount, message.Authorities);
        ReadRecords(buffer, length, ref offset, additionalCount, message.Additionals);

        return message;
    }

    private static void ReadRecords(byte[] buffer, int length, ref int offset, int count, List<DnsResource> into)
    {
        for (int i = 0; i < count; i++)
        {
            string name = ReadName(buffer, length, ref offset);
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(offset, 2));
            ushort cls = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(offset + 2, 2));
            uint ttl = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(offset + 4, 4));
            ushort rdLength = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(offset + 8, 2));
            offset += 10;

            if (offset + rdLength > length) throw new InvalidDataException("DNS 记录超出报文长度。");

            byte[] rdata = buffer.AsSpan(offset, rdLength).ToArray();
            string? target = null;

            if (type is (ushort)DnsRecordType.PTR)
            {
                int cursor = offset;
                target = ReadName(buffer, length, ref cursor);
            }
            else if (type is (ushort)DnsRecordType.SRV && rdLength > 6)
            {
                int cursor = offset + 6;
                target = ReadName(buffer, length, ref cursor);
            }

            offset += rdLength;
            into.Add(new DnsResource
            {
                Name = name,
                Type = (DnsRecordType)type,
                Class = cls,
                Ttl = ttl,
                RData = rdata,
                Target = target
            });
        }
    }

    /// <summary>Reads a possibly compressed name; the caller's offset moves past the name in the record.</summary>
    public static string ReadName(byte[] buffer, int length, ref int offset)
    {
        var labels = new List<string>();
        int cursor = offset;
        int resume = -1;
        int depth = 0;

        while (true)
        {
            if (cursor >= length) throw new InvalidDataException("DNS 名称越界。");

            byte lengthByte = buffer[cursor];

            if ((lengthByte & 0xC0) == 0xC0)
            {
                if (cursor + 1 >= length) throw new InvalidDataException("DNS 压缩指针不完整。");
                if (resume < 0) resume = cursor + 2;

                int pointer = ((lengthByte & 0x3F) << 8) | buffer[cursor + 1];
                if (pointer >= length) throw new InvalidDataException("DNS 压缩指针越界。");
                if (++depth > MaxNameDepth) throw new InvalidDataException("DNS 名称压缩层级过深。");

                cursor = pointer;
                continue;
            }

            if (lengthByte == 0)
            {
                cursor++;
                break;
            }

            if ((lengthByte & 0xC0) != 0) throw new InvalidDataException("DNS 名称标签长度非法。");
            if (cursor + 1 + lengthByte > length) throw new InvalidDataException("DNS 名称标签越界。");

            labels.Add(Encoding.ASCII.GetString(buffer, cursor + 1, lengthByte));
            cursor += 1 + lengthByte;
        }

        offset = resume >= 0 ? resume : cursor;
        return string.Join('.', labels);
    }
}
