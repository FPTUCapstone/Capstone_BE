namespace TripMate.Application.Common.Interfaces;

public interface IPushTokenProtector
{
    string Protect(string plaintext);

    string Unprotect(string protectedValue);
}