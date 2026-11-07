using System;
using System.Linq;
using System.Threading.Tasks;
using ChromeSidecarStandalone.Services;
using ChromeSidecarStandalone.Util;

namespace ChromeSidecarStandalone
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Log.Init();
            try
            {
                return RunAsync(args ?? new string[0]).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("AI Chat Bridge failed: " + ex.Message);
                return 1;
            }
            finally
            {
                Log.Shutdown();
            }
        }

        private static async Task<int> RunAsync(string[] args)
        {
            if (args.Any(a => string.Equals(a, "--help", StringComparison.OrdinalIgnoreCase)
                || string.Equals(a, "-h", StringComparison.OrdinalIgnoreCase)))
            {
                PrintUsage();
                return 0;
            }

            var provider = GetOptionValue(args, "--provider");
            if (args.Any(a => string.Equals(a, "--start", StringComparison.OrdinalIgnoreCase)))
            {
                using (var sidecar = CreateSidecar())
                {
                    await sidecar.StartChromeAsync(provider).ConfigureAwait(false);
                    Console.WriteLine("started=true");
                    return 0;
                }
            }

            if (args.Any(a => string.Equals(a, "--stop", StringComparison.OrdinalIgnoreCase)))
            {
                using (var sidecar = CreateSidecar())
                {
                    sidecar.StopChrome();
                    Console.WriteLine("stopped=true");
                    return 0;
                }
            }

            if (args.Any(a => string.Equals(a, "--new", StringComparison.OrdinalIgnoreCase)))
            {
                using (var sidecar = CreateSidecar())
                {
                    var created = await sidecar.StartNewChatAsync(provider).ConfigureAwait(false);
                    Console.WriteLine("newChat=" + created);
                    return created ? 0 : 1;
                }
            }

            if (args.Any(a => string.Equals(a, "--close", StringComparison.OrdinalIgnoreCase)))
            {
                using (var sidecar = CreateSidecar())
                {
                    var closed = await sidecar.CloseAllTabsAsync().ConfigureAwait(false);
                    Console.WriteLine("closedTabs=" + closed);
                    return 0;
                }
            }

            if (args.Any(a => string.Equals(a, "--show", StringComparison.OrdinalIgnoreCase)))
                return SetVisibility(true);

            if (args.Any(a => string.Equals(a, "--hide", StringComparison.OrdinalIgnoreCase)))
                return SetVisibility(false);

            using (var sidecar = CreateSidecar())
            {
                sidecar.OnLog += message => Console.Error.WriteLine(message);

                if (args.Any(a => string.Equals(a, "--status", StringComparison.OrdinalIgnoreCase)))
                {
                    Console.WriteLine("connected=" + sidecar.IsConnected);
                    Console.WriteLine("visible=" + sidecar.IsManagedChromeVisible());
                    foreach (var snapshot in ChromeSidecar.GetProviderRateLimitSnapshots())
                        Console.WriteLine(snapshot.provider + "=" + snapshot.state);
                    return 0;
                }

                var prompt = GetOptionValue(args, "--query");
                if (!string.IsNullOrWhiteSpace(prompt))
                {
                    var response = string.IsNullOrWhiteSpace(provider)
                        ? await sidecar.QueryAIAsync(prompt).ConfigureAwait(false)
                        : await sidecar.QueryAIAsync(prompt, provider).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(response))
                    {
                        Console.Error.WriteLine("No AI response received: " + sidecar.LastFailureReason);
                        return 1;
                    }

                    Console.WriteLine(response);
                    return 0;
                }

                if (args.Any(a => string.Equals(a, "--connect", StringComparison.OrdinalIgnoreCase)))
                {
                    var connected = string.IsNullOrWhiteSpace(provider)
                        ? await sidecar.ConnectAsync().ConfigureAwait(false)
                        : await sidecar.ConnectAsync(provider).ConfigureAwait(false);
                    Console.WriteLine("connected=" + connected);
                    return connected ? 0 : 1;
                }
            }

            PrintUsage();
            return 2;
        }

        private static int SetVisibility(bool visible)
        {
            using (var sidecar = CreateSidecar())
            {
                var applied = sidecar.SetManagedChromeVisible(visible);
                Console.WriteLine("visible=" + sidecar.IsManagedChromeVisible());
                return applied ? 0 : 1;
            }
        }

        private static string GetOptionValue(string[] args, string option)
        {
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], option, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }

            return null;
        }

        private static ChromeSidecar CreateSidecar()
        {
            return new ChromeSidecar
            {
                KeepManagedChromeAlive = true,
                UseFreshChatForQueries = false
            };
        }

        private static void PrintUsage()
        {
            Console.WriteLine("AI Chat Bridge");
            Console.WriteLine("  --start [--provider chatgpt|gemini|claude]");
            Console.WriteLine("  --stop");
            Console.WriteLine("  --new [--provider chatgpt|gemini|claude]");
            Console.WriteLine("  --close");
            Console.WriteLine("  --connect [--provider chatgpt|gemini|claude]");
            Console.WriteLine("  --query <prompt> [--provider chatgpt|gemini|claude]");
            Console.WriteLine("  --status");
            Console.WriteLine("  --show | --hide");
        }
    }
}
