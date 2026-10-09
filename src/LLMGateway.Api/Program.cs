using LLMGateway.Core;
using LLMGateway.Native;
using LLMGateway.Server;

var builder = WebApplication.CreateBuilder(args);
var gatewayOptions = builder.Configuration.GetSection("LLMGateway").Get<GatewayOptions>() ?? new GatewayOptions();
var serverOptions = builder.Configuration.GetSection("LLMGateway:Server").Get<GatewayServerOptions>() ?? new GatewayServerOptions();
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = serverOptions.MaxRequestBodyBytes);
builder.Services.AddLlmGateway(gatewayOptions);
builder.Services.AddSingleton(serverOptions);

var app = builder.Build();
app.MapLlmGateway(serverOptions);
app.Run();
