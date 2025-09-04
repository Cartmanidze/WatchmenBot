using System.Net;

namespace WatchmenBot.Extensions;

public static class TelegramSecurityExtensions
{
    public static (bool IsValid, string Reason) ValidateSecretToken(
        this IHeaderDictionary headers, 
        string? expectedSecret)
    {
        if (string.IsNullOrWhiteSpace(expectedSecret))
            return (true, "No secret configured");

        if (!headers.TryGetValue("X-Telegram-Bot-Api-Secret-Token", out var providedSecret))
            return (false, "Missing secret token header");

        if (providedSecret.FirstOrDefault() != expectedSecret)
            return (false, "Invalid secret token");

        return (true, "Valid secret token");
    }

    public static (bool IsValid, string Reason) ValidateIpRange(
        this IPAddress? remoteIp, 
        bool validateIpEnabled)
    {
        if (!validateIpEnabled)
            return (true, "IP validation disabled");

        if (remoteIp == null)
            return (true, "No remote IP");

        return remoteIp.IsValidTelegramIp() 
            ? (true, "Valid Telegram IP") 
            : (false, "Invalid IP range");
    }

    public static bool IsValidTelegramIp(this IPAddress ipAddress)
    {
        // Telegram IP ranges: 149.154.160.0/20 and 91.108.4.0/22
        var telegramRanges = new[]
        {
            new { Network = IPAddress.Parse("149.154.160.0"), PrefixLength = 20 },
            new { Network = IPAddress.Parse("91.108.4.0"), PrefixLength = 22 }
        };

        return telegramRanges.Any(range => ipAddress.IsInSubnet(range.Network, range.PrefixLength));
    }

    public static bool IsInSubnet(this IPAddress address, IPAddress network, int prefixLength)
    {
        if (address.AddressFamily != network.AddressFamily)
            return false;

        var addressBytes = address.GetAddressBytes();
        var networkBytes = network.GetAddressBytes();
        
        var bytesToCheck = prefixLength / 8;
        var bitsToCheck = prefixLength % 8;

        for (int i = 0; i < bytesToCheck; i++)
        {
            if (addressBytes[i] != networkBytes[i])
                return false;
        }

        if (bitsToCheck > 0)
        {
            var mask = (byte)(0xFF << (8 - bitsToCheck));
            if ((addressBytes[bytesToCheck] & mask) != (networkBytes[bytesToCheck] & mask))
                return false;
        }

        return true;
    }
}