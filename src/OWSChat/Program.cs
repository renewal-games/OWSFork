// Program.cs  –  .NET 8 minimal‑hosting style
using Grpc.AspNetCore.Server;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using SimpleInjector;
using OWSChat.Service;
using OWSShared.Middleware;
using OWSShared.Implementations;
using OWSShared.Interfaces;
using Microsoft.OpenApi.Models;
using System.IO;
using Microsoft.AspNetCore.Builder;
using System;
using Microsoft.Extensions.DependencyInjection;
using OWSChat;
using Microsoft.AspNetCore.Hosting;
using ProtoBuf.Grpc.Server;
using Microsoft.AspNetCore.DataProtection;

var container = new Container();

var builder = WebApplication.CreateBuilder(args);


builder.Services.AddDataProtection()
                .PersistKeysToFileSystem(new DirectoryInfo("./temp/DataProtection-Keys"));
builder.Services.AddMemoryCache();
builder.Services.AddHttpContextAccessor();
builder.Services.AddMvcCore(o => o.EnableEndpointRouting = false)
                .AddViews()
                .AddApiExplorer();

builder.Services.AddGrpc();             
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Chat Auth API", Version = "v1" });
});
builder.Services.AddSimpleInjector(container, o => o.AddAspNetCore()
                                                    .AddControllerActivation());

builder.Services.AddCodeFirstGrpc();
builder.Services.AddSingleton(typeof(IGrpcServiceActivator<>),
                               typeof(GrpcSimpleInjectorActivator<>));


container.RegisterSingleton<ChatService>();
container.Register<IHeaderCustomerGUID, HeaderCustomerGUID>(Lifestyle.Scoped);


builder.WebHost.ConfigureKestrel(k =>
{
    k.ListenAnyIP(50051, o =>
    {
        o.Protocols = HttpProtocols.Http2; 
    });
});


var app = builder.Build();
container.RegisterInstance<IServiceProvider>(app.Services);
app.Services.UseSimpleInjector(container);

app.UseMiddleware<StoreCustomerGUIDMiddleware>(container);

app.UseRouting();
app.UseMvc();          
app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("./v1/swagger.json", "Chat Auth API"));

app.MapGrpcService<ChatService>();
app.MapGet("/", () => "gRPC server is running ‑ connect with UE client");

container.Verify();
app.Run();
