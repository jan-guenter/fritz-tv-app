namespace FritzTV.Infrastructure.Dvb;

public sealed class PsiSectionAssembler
{
    private readonly Dictionary<int, AssemblyState> states = [];

    public event EventHandler<PsiSectionEventArgs>? SectionReceived;

    public void Push(ReadOnlySpan<byte> packet)
    {
        if (!TsPacket.TryParse(packet, out var ts) || ts.PayloadOffset >= packet.Length)
        {
            return;
        }

        if (!states.TryGetValue(ts.Pid, out var state))
        {
            state = new AssemblyState();
            states.Add(ts.Pid, state);
        }

        if (ts.Discontinuity || (state.Continuity is { } continuity && ts.Continuity != ((continuity + 1) & 15)))
        {
            if (state.Continuity == ts.Continuity && !ts.Discontinuity)
            {
                return;
            }

            state.Length = 0;
        }

        state.Continuity = ts.Continuity;
        var payload = packet[ts.PayloadOffset..];
        if (ts.StartsPayload)
        {
            var pointer = payload[0];
            if (pointer >= payload.Length)
            {
                state.Length = 0;
                return;
            }

            if (state.Length > 0 && pointer > 0)
            {
                Append(ts.Pid, state, payload.Slice(1, pointer));
            }

            state.Length = 0;
            Append(ts.Pid, state, payload[(pointer + 1)..]);
        }
        else if (state.Length > 0)
        {
            Append(ts.Pid, state, payload);
        }
    }

    private void Append(int pid, AssemblyState state, ReadOnlySpan<byte> data)
    {
        if (data.Length + state.Length > state.Buffer.Length)
        {
            state.Length = 0;
            return;
        }

        data.CopyTo(state.Buffer.AsSpan(state.Length));
        state.Length += data.Length;
        while (state.Length >= 3)
        {
            if (state.Buffer[0] == 0xff)
            {
                state.Length = 0;
                break;
            }

            var sectionLength = 3 + (((state.Buffer[1] & 15) << 8) | state.Buffer[2]);
            if (sectionLength is < 3 or > 4096)
            {
                state.Length = 0;
                break;
            }

            if (state.Length < sectionLength)
            {
                break;
            }

            var section = state.Buffer.AsSpan(0, sectionLength);
            if (sectionLength >= 4 && MpegCrc32.Compute(section) == 0)
            {
                SectionReceived?.Invoke(this, new PsiSectionEventArgs(pid, section.ToArray()));
            }

            state.Length -= sectionLength;
            state.Buffer.AsSpan(sectionLength, state.Length).CopyTo(state.Buffer);
        }
    }

    private sealed class AssemblyState
    {
        public byte[] Buffer { get; } = new byte[8192];

        public int Length { get; set; }

        public int? Continuity { get; set; }
    }
}

