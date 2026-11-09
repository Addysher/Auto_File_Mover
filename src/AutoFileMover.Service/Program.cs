using AutoFileMover.Core;
using AutoFileMover.Service;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options => options.ServiceName = AppPaths.ServiceName);
builder.Services.AddHostedService<MoverWorker>();

builder.Build().Run();
