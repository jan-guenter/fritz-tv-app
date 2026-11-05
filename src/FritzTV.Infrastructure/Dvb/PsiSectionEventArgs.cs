namespace FritzTV.Infrastructure.Dvb;

public sealed class PsiSectionEventArgs(int pid, ReadOnlyMemory<byte> data) : EventArgs
{
    public int Pid { get; } = pid;

    public ReadOnlyMemory<byte> Data { get; } = data;
}
