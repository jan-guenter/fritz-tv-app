namespace FritzTV.Core.Exceptions;

public sealed class TimeshiftStorageException : IOException
{
    public TimeshiftStorageException()
        : base("The timeshift storage could not accept more transport data.")
    {
    }

    public TimeshiftStorageException(string message)
        : base(message)
    {
    }

    public TimeshiftStorageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
