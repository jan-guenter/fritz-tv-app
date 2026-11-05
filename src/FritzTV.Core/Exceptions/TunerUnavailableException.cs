namespace FritzTV.Core.Exceptions;

public sealed class TunerUnavailableException : IOException
{
    public TunerUnavailableException()
        : base("No SAT>IP tuner is available without interrupting an equal or higher priority consumer.")
    {
    }

    public TunerUnavailableException(string message)
        : base(message)
    {
    }

    public TunerUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
