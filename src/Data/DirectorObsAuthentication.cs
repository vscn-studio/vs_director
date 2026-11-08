using System.Security.Cryptography;
using System.Text;

namespace VSDirector.Data;

public static class DirectorObsAuthentication
{
    public static string Compute(string password, string salt, string challenge)
    {
        string first = Convert.ToBase64String(
            SHA256.HashData(Encoding.UTF8.GetBytes((password ?? string.Empty) + (salt ?? string.Empty))));
        return Convert.ToBase64String(
            SHA256.HashData(Encoding.UTF8.GetBytes(first + (challenge ?? string.Empty))));
    }
}
