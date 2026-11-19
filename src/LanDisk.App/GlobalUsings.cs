// The WPF SDK's implicit usings are narrower than the console SDK's: System.IO and System.Net.Http are
// not included, and the shell needs both (log files, readiness probe). The product namespaces are
// included here as well so a page does not have to remember where BuildInfo or the shared operations
// live.
global using System.IO;
global using System.Net.Http;
global using LanDisk.Core.Configuration;
global using LanDisk.Server.Endpoints;
global using LanDisk.Server.Services;
