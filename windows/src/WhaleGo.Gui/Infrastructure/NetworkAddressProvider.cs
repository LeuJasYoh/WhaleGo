using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace WhaleGo.Gui.Infrastructure;

/// <summary>Lists candidate listen addresses (docs/GUI.md section 9.6: prefer the Docker/WSL host adapter IP).</summary>
public static class NetworkAddressProvider
{
    public static IReadOnlyList<string> ListLocalIPv4Addresses()
    {
        var addresses = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    var text = unicast.Address.ToString();
                    if (!addresses.Contains(text))
                    {
                        addresses.Add(text);
                    }
                }
            }
        }
        catch
        {
            // fall through: only the fixed options are offered
        }

        return addresses;
    }

    public static IReadOnlyList<string> ListenAddressOptions()
    {
        var options = new List<string> { "0.0.0.0", "127.0.0.1" };
        options.AddRange(ListLocalIPv4Addresses());
        return options;
    }
}
