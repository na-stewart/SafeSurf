using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.Win32.TaskScheduler;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using Task = System.Threading.Tasks.Task;
using System.ServiceProcess;

/*
MIT License

Copyright (c) 2024 Nicholas Aidan Stewart

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

namespace Enforcer
{
    public partial class Main : Form
    {
        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool ShutdownBlockReasonCreate(IntPtr hWnd, [MarshalAs(UnmanagedType.LPWStr)] string pwszReason);
        readonly string windowsPath = "C:\\WINDOWS\\System32";
        readonly ServiceController watchdog = new ServiceController("SSWatchdog");
        //readonly string exePath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        readonly string exePath = "C:\\Users\\Aidan Stewart\\Source\\Repos\\na-stewart\\FreeSafeSurf\\bin";
        readonly Config config = Config.Instance;
        readonly List<FileStream> filePadlocks = new List<FileStream> ();
        bool isEnforcerActive = true;

        public Main(string[] args)
        {
            InitializeComponent();
            
            if (config.Read("days-enforced").Equals("0"))
            {
                isEnforcerActive = false;
                SetHosts();
                SetCleanBrowsingDNS();
            }
            else
            {     
                SetHosts();
                AddDefenderExclusion();
                ShutdownBlockReasonCreate(Handle, "Enforcer is active.");
                InitializeWatchdog(args);
                InitializeLock();      
            }
            Environment.Exit(0);
        }

        void InitializeLock()
        {
            filePadlocks.Add(new FileStream(config.ConfigFile, FileMode.Open, FileAccess.Read, FileShare.Read));
            foreach (var file in Directory.GetFiles(exePath, "*", SearchOption.AllDirectories))
                filePadlocks.Add(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read));
            while (isEnforcerActive)
            {
                if (IsExpired())
                {
                    isEnforcerActive = false;
                    foreach (var filePadlock in filePadlocks)
                        filePadlock.Close();
                    using (var taskService = new TaskService()) 
                        taskService.RootFolder.DeleteTask("SafeSurf");
                   
                    continue;
                }
                else
                {  
                    SetCleanBrowsingDNS();      
                    RegisterStartupTask();       
                }
                Thread.Sleep(4000);
            }
        }

        DateTime? GetNetworkTime()
        {
            DateTime? networkDateTime = null;
            try
            {
                var client = new TcpClient("time.nist.gov", 13);
                using (var streamReader = new StreamReader(client.GetStream()))
                {
                    var response = streamReader.ReadToEnd();
                    var utcDateTimeString = response.Substring(7, 17);
                    networkDateTime = DateTime.ParseExact(utcDateTimeString, "yy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
                }
            }
            catch (SocketException) { }
            catch (ArgumentOutOfRangeException) { }
            return networkDateTime;
        }

        bool IsExpired()
        {
            DateTime.TryParse(config.Read("date-enforced"), out DateTime parsedDateEnforced);
            var networkTime = GetNetworkTime();
            var expirationDate = parsedDateEnforced.AddMinutes(int.Parse(config.Read("days-enforced")));
            return networkTime == null ? false : networkTime >= expirationDate;
        }



        void InitializeWatchdog(string[] args)
        {
            try
            {
                watchdog.Start(new string[] { Process.GetCurrentProcess().Id.ToString() });
            }
            catch (InvalidOperationException) 
            {
                using (Process installer = new Process())
                {
                    installer.StartInfo.FileName = Path.Combine(exePath, "WatchdogService.exe");
                    installer.StartInfo.Arguments = "-i";
                    installer.Start();
                    installer.WaitForExit();
                    watchdog.Start(new string[]{Process.GetCurrentProcess().Id.ToString()});
                }
            }
        }

        void RegisterStartupTask()
        {
            using (var taskService = new TaskService())
            {
                taskService.RootFolder.DeleteTask("SafeSurf", false);
                var taskDefinition = taskService.NewTask();
                taskDefinition.Settings.DisallowStartIfOnBatteries = false;
                taskDefinition.RegistrationInfo.Description = "SafeSurf startup and heartbeat task.";
                taskDefinition.RegistrationInfo.Author = "github.com/na-stewart";
                taskDefinition.Principal.RunLevel = TaskRunLevel.Highest;
                taskDefinition.Triggers.Add(new LogonTrigger());
                taskDefinition.Triggers.Add(new TimeTrigger()
                {
                    StartBoundary = DateTime.Now,
                    Repetition = new RepetitionPattern(TimeSpan.FromMinutes(1), TimeSpan.Zero)
                });
                taskDefinition.Actions.Add(new ExecAction(Path.Combine(exePath, "SSDaemon.exe")));
                taskService.RootFolder.RegisterTaskDefinition("SafeSurf", taskDefinition);
            }
        }

        void SetCleanBrowsingDNS()
        {
            try
            {
                string[]? dns;
                if (!isEnforcerActive && config.Read("cleanbrowsing-dns-filter").Equals("off"))
                    dns = null;
                else if (config.Read("cleanbrowsing-dns-filter").Equals("family"))
                    dns = new string[] { "185.228.168.168", "185.228.169.168" };
                else if (config.Read("cleanbrowsing-dns-filter").Equals("adult"))
                    dns = new string[] { "185.228.168.10", "185.228.169.11" };
                else
                    return;
                var currentInterface = GetActiveEthernetOrWifiNetworkInterface();
                if (currentInterface == null) return;
                foreach (ManagementObject objMO in new ManagementClass("Win32_NetworkAdapterConfiguration").GetInstances())
                {
                    if ((bool)objMO["IPEnabled"])
                    {
                        if (objMO["Description"].Equals(currentInterface.Description))
                        {
                            var objdns = objMO.GetMethodParameters("SetDNSServerSearchOrder");
                            if (objdns != null)
                            {
                                objdns["DNSServerSearchOrder"] = dns;
                                objMO.InvokeMethod("SetDNSServerSearchOrder", objdns, null);
                            }
                        }
                    }
                }
            }
            catch (FileLoadException) { }
        }

        NetworkInterface? GetActiveEthernetOrWifiNetworkInterface()
        {
            return NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(
                a => a.OperationalStatus == OperationalStatus.Up &&
                (a.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 || a.NetworkInterfaceType == NetworkInterfaceType.Ethernet) &&
                a.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily.ToString().Equals("InterNetwork")));
        }

        void SetHosts()
        {
            var filterHosts = Path.Combine(exePath, $"{config.Read("hosts-filter")}.hosts");
            var hosts = Path.Combine(windowsPath, "drivers\\etc\\hosts");
            try
            {
                if (config.Read("hosts-filter").Equals("off"))
                {
                    if (!isEnforcerActive)
                        File.WriteAllText(hosts, string.Empty);
                }
                else
                {
                    File.WriteAllText(hosts, File.ReadAllText(filterHosts));
                    filePadlocks.Add(new FileStream(hosts, FileMode.Open, FileAccess.Read, FileShare.Read));
                }
            }
            catch (IOException) { }
        }

        void AddDefenderExclusion()
        {
            var powershell = new ProcessStartInfo("powershell")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                Verb = "runas",
                Arguments = $" -Command Add-MpPreference -ExclusionPath '{exePath}'"
            };
            Process.Start(powershell);
        }

        public void ShowMotivation()
        {
            string[] quotes = new string[] {     
                "You can either suffer the pain of discipline or live with the pain of regret.",
                "Strive to become who you want to be and don't allow hardship to divert you from this path.",
                "Treat each day as a new life, and at once begin to live.",
                "If you stop bad habits now, years will pass and your regrets will soon be far behind you.",
                "There are no regrets in life, just lessons learned. You must do right by yourself to not repeat mistakes.",
                "Ever tried, ever failed. No matter. Try again, fail again, fail better!",
                "The only person you are destined to become is who you decide to be.",
                "I'm not telling you it is going to be easy. Im telling you it's going to be worth it! Wake up and live!",
                "Hardships often prepare ordinary people for extraordinary things. Don't let it tear you down.",
                "Be stronger than your strongest excuse or suffer the consequences.",
                "Success is the sum of small efforts and sacrifices, repeated day in and day out. That is how you contribute towards a fulfilling life.",
                "Bad habits are broken effectively when traded for good habits.",
                "Regret born of ill-fated choices will surpass all other hardships.",
                "Act as if what you do makes a difference, it does. Decisions result in consequences, both good and bad."
            };
            new ToastContentBuilder().AddText("SafeSurf - Circumvention Detected").AddText(quotes[new Random().Next(0, quotes.Count())]).Show();
        }

        protected override void WndProc(ref Message aMessage)
        {
            if (aMessage.Msg == 0x0011)
                return;
            base.WndProc(ref aMessage);
        }
    }
}
