using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using UnityBridge.Api;
using UnityBridge.Configuration;
using UnityBridge.Jobs;
using UnityBridge.Unity;
using UnityBridge.Worker;

var builder = Host.CreateApplicationBuilder(args);

var settings = BridgeSettings.Load(builder.Configuration);

// configure container
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<JobManager>();
builder.Services.AddSingleton<UnityRunner>();
builder.Services.AddHostedService<BridgeWorker>();
builder.Services.AddHostedService<HttpServer>();


await builder.Build().RunAsync();
