using System.Security.Cryptography;
using System.Text.Json;

namespace Agwinterm.Pty;

public static class AgtermPageStorage
{
    // Credentials are excluded, but every origin routing boundary participates in the partition.
    public static string Partition(AgtermRemoteBinding binding) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            binding.Target.Distribution, binding.Target.User, binding.Target.Destination,
            binding.Target.OriginSocket, binding.Endpoint, binding.OriginWindow, binding.OriginSession
        })));
}
