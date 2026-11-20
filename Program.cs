using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using UnityBridge.Api;
using UnityBridge.Configuration;
using UnityBridge.Jobs;
using UnityBridge.Unity;
using UnityBridge.Worker;

// Create the host and load the validated bridge configuration.
var v_builder = Host.CreateApplicationBuilder(args);

var v_settings = CBridgeSettings.Load(v_builder.Configuration);

// Register the shared queue, Unity runner, and hosted services.
v_builder.Services.AddSingleton(v_settings);
v_builder.Services.AddSingleton<CJobManager>();
v_builder.Services.AddSingleton<CUnityRunner>();
v_builder.Services.AddHostedService<CBridgeWorker>();
v_builder.Services.AddHostedService<CHttpServer>();

await v_builder.Build().RunAsync();
