using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NovaTouchUpdater;

// Read-only NBSTAT query (RFC 1002, 4.2.17/18), including workgroup PCs without reverse DNS.
internal static class NetBiosNames
{
    internal static byte[] CreateQuery(ushort transaction)
    {
        byte[] packet = new byte[50];
        BinaryPrimitives.WriteUInt16BigEndian(packet, transaction);
        packet[5] = 1; // one question
        packet[12] = 32;
        for (int i = 0; i < 16; i++)
        {
            byte value = i == 0 ? (byte)'*' : (byte)0;
            packet[13 + i * 2] = (byte)('A' + (value >> 4));
            packet[14 + i * 2] = (byte)('A' + (value & 15));
        }
        packet[47] = 0x21; packet[49] = 1; // NBSTAT, IN
        return packet;
    }

    internal static async Task<string?> ResolveAsync(IPAddress address, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(900));
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            udp.Connect(address, 137);
            ushort transaction = (ushort)Random.Shared.Next(ushort.MaxValue + 1);
            await udp.SendAsync(CreateQuery(transaction), timeout.Token).ConfigureAwait(false);
            var reply = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);
            return ReadName(reply.Buffer, transaction);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (SocketException) { return null; }
    }

    internal static string? ReadName(ReadOnlySpan<byte> packet, ushort transaction)
    {
        if (packet.Length < 12 || Read16(packet, 0) != transaction || (Read16(packet, 2) & 0x800F) != 0x8000) return null;
        int offset = 12;
        for (int i = 0; i < Read16(packet, 4); i++)
        {
            if (!SkipName(packet, ref offset) || offset + 4 > packet.Length) return null;
            offset += 4;
        }
        for (int answer = 0; answer < Read16(packet, 6); answer++)
        {
            if (!SkipName(packet, ref offset) || offset + 10 > packet.Length) return null;
            int type = Read16(packet, offset), recordClass = Read16(packet, offset + 2), length = Read16(packet, offset + 8);
            offset += 10;
            if (offset + length > packet.Length) return null;
            if (type == 0x21 && recordClass == 1 && length > 0)
            {
                int count = packet[offset];
                if (1 + count * 18 > length) return null;
                for (int i = 0; i < count; i++)
                {
                    int item = offset + 1 + i * 18;
                    if (packet[item + 15] != 0 || (Read16(packet, item + 16) & 0x8000) != 0) continue;
                    string name = Encoding.ASCII.GetString(packet.Slice(item, 15)).TrimEnd(' ', '\0');
                    if (name.Length > 0 && name != "*" && name.All(c => c > ' ' && c < 127)) return name;
                }
            }
            offset += length;
        }
        return null;
    }

    private static ushort Read16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);

    private static bool SkipName(ReadOnlySpan<byte> data, ref int offset)
    {
        while (offset < data.Length)
        {
            int length = data[offset++];
            if (length == 0) return true;
            if ((length & 0xC0) == 0xC0) { if (offset >= data.Length) return false; offset++; return true; }
            if (length > 63 || offset + length > data.Length) return false;
            offset += length;
        }
        return false;
    }
}
