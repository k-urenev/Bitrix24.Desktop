using System.Text;
using System.Text.Json.Nodes;

namespace Bitrix24.Desktop.Core.Polling;

public static class PullFrame
{
    public static string ToJson(ReadOnlySpan<byte> frame)
    {
        try
        {
            var items = new JsonArray();
            var reader = new ProtoReader(frame);
            while (reader.Remaining)
            {
                if (!reader.ReadTag(out var field, out var wire))
                    break;
                if (field == 1 && wire == 2)
                    ReadResponse(reader.ReadBytes(), items);
                else
                    reader.Skip(wire);
            }

            return items.ToJsonString();
        }
        catch (Exception exception)
        {
            FileLog.Write(exception);
            return "[]";
        }
    }

    private static void ReadResponse(ReadOnlySpan<byte> message, JsonArray items)
    {
        var reader = new ProtoReader(message);
        while (reader.Remaining)
        {
            if (!reader.ReadTag(out var field, out var wire))
                break;
            if (field == 1 && wire == 2)
                ReadOutgoing(reader.ReadBytes(), items);
            else
                reader.Skip(wire);
        }
    }

    private static void ReadOutgoing(ReadOnlySpan<byte> message, JsonArray items)
    {
        var reader = new ProtoReader(message);
        while (reader.Remaining)
        {
            if (!reader.ReadTag(out var field, out var wire))
                break;
            if (field == 1 && wire == 2)
                ReadMessage(reader.ReadBytes(), items);
            else
                reader.Skip(wire);
        }
    }

    private static void ReadMessage(ReadOnlySpan<byte> message, JsonArray items)
    {
        var reader = new ProtoReader(message);
        byte[]? id = null;
        string? body = null;
        while (reader.Remaining)
        {
            if (!reader.ReadTag(out var field, out var wire))
                break;
            if (field == 1 && wire == 2)
                id = reader.ReadBytes().ToArray();
            else if (field == 2 && wire == 2)
                body = reader.ReadString();
            else
                reader.Skip(wire);
        }

        if (string.IsNullOrWhiteSpace(body))
            return;

        JsonNode? text;
        try
        {
            text = JsonNode.Parse(body);
        }
        catch (System.Text.Json.JsonException)
        {
            return;
        }

        if (text is null)
            return;

        items.Add(new JsonObject
        {
            ["mid"] = id is null ? "" : Convert.ToHexString(id).ToLowerInvariant(),
            ["text"] = text
        });
    }

    private ref struct ProtoReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _pos;

        public ProtoReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _pos = 0;
        }

        public bool Remaining => _pos < _data.Length;

        public bool ReadTag(out int field, out int wire)
        {
            if (!TryVarint(out var tag))
            {
                field = 0;
                wire = 0;
                return false;
            }

            field = (int)(tag >> 3);
            wire = (int)(tag & 7);
            return field > 0;
        }

        public ReadOnlySpan<byte> ReadBytes()
        {
            var length = checked((int)ReadVarint());
            if (length < 0 || _pos + length > _data.Length)
                throw new InvalidDataException();
            var slice = _data.Slice(_pos, length);
            _pos += length;
            return slice;
        }

        public string ReadString() => Encoding.UTF8.GetString(ReadBytes());

        public void Skip(int wire)
        {
            switch (wire)
            {
                case 0:
                    ReadVarint();
                    break;
                case 1:
                    Advance(8);
                    break;
                case 2:
                    Advance(checked((int)ReadVarint()));
                    break;
                case 5:
                    Advance(4);
                    break;
                default:
                    throw new InvalidDataException();
            }
        }

        private void Advance(int count)
        {
            if (count < 0 || _pos + count > _data.Length)
                throw new InvalidDataException();
            _pos += count;
        }

        private ulong ReadVarint()
        {
            if (!TryVarint(out var value))
                throw new InvalidDataException();
            return value;
        }

        private bool TryVarint(out ulong value)
        {
            value = 0;
            var shift = 0;
            while (_pos < _data.Length && shift < 64)
            {
                var byteValue = _data[_pos++];
                value |= (ulong)(byteValue & 0x7F) << shift;
                if ((byteValue & 0x80) == 0)
                    return true;
                shift += 7;
            }

            return false;
        }
    }
}
