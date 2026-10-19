// 3.1.0 (.NET 10): the runtime has its own System.Diagnostics.Activity; every file that imports System.Diagnostics
// means the Outpost's activity tracker (Services/Activity.cs).
global using Activity = ElansAddonHub.Services.Activity;
