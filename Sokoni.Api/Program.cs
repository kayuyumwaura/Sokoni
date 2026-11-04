using Sokoni;
using Sokoni.Agent;
using Sokoni.Channels;
using Sokoni.Data;
using Sokoni.Payments;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<MuseOptions>(builder.Configuration.GetSection("MetaModelApi"));
builder.Services.Configure<WhatsAppOptions>(builder.Configuration.GetSection("WhatsApp"));
builder.Services.Configure<MpesaOptions>(builder.Configuration.GetSection("Mpesa"));
builder.Services.Configure<BusinessOptions>(builder.Configuration.GetSection("Business"));
builder.Services.Configure<SecurityOptions>(builder.Configuration.GetSection("Security"));

builder.Services.AddControllers();

// Data (raw ADO.NET)
builder.Services.AddSingleton<Db>();
builder.Services.AddScoped<ProductRepository>();
builder.Services.AddScoped<ConversationRepository>();
builder.Services.AddScoped<OrderRepository>();

// Outside services
builder.Services.AddHttpClient<MuseClient>();
builder.Services.AddHttpClient<WhatsAppChannel>();
builder.Services.AddHttpClient<MpesaClient>();
builder.Services.AddHttpClient<MediaUnderstanding>();

// Channels
builder.Services.AddSingleton<DevChannel>();
builder.Services.AddScoped<ChannelRouter>();
builder.Services.AddSingleton<InboundQueue>();
builder.Services.AddHostedService<InboundWorker>();

// Agent + payments
builder.Services.AddScoped<SalesTools>();
builder.Services.AddScoped<SalesAgent>();
builder.Services.AddScoped<MessageProcessor>();
builder.Services.AddScoped<OrderNotifier>();

var app = builder.Build();

if (app.Configuration.GetValue("Database:ApplySchemaOnStartup", true))
{
    await app.Services.GetRequiredService<Db>().EnsureSchemaAsync();
}

// Owner dashboard: wwwroot/admin/index.html, served at /admin/
app.Use(async (ctx, next) =>
{
    // Exact "/admin" → "/admin/" (a mapped route would also catch "/admin/" and loop).
    if (ctx.Request.Path.Value == "/admin") { ctx.Response.Redirect("/admin/"); return; }
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/", () => Results.Text("Sokoni is running. Owner dashboard: /admin"));
app.MapGet("/health", () => Results.Ok(new { status = "ok", utc = DateTime.UtcNow }));
app.MapControllers();

app.Run();
