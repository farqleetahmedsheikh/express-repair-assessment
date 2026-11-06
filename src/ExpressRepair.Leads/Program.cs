using ExpressRepair.Leads.Ai;
using ExpressRepair.Leads.Data;
using ExpressRepair.Leads.Processing;
using ExpressRepair.Leads.Realtime;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Web
builder.Services.AddRazorPages();
builder.Services.AddControllers();
builder.Services.AddSignalR();

// Data (SQLite file, created on first run)
builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseSqlite(builder.Configuration.GetConnectionString("Calls")));

// Background processing
builder.Services.Configure<ProcessingOptions>(builder.Configuration.GetSection(ProcessingOptions.Section));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ICallQueue, ChannelCallQueue>();
builder.Services.AddSingleton<ICallNotifier, SignalRCallNotifier>();
builder.Services.AddScoped<CallProcessor>();
builder.Services.AddHostedService<CallProcessingWorker>();

// AI: Mock or OpenAI per interface, chosen from config
builder.Services.AddAiProviders(builder.Configuration);

var app = builder.Build();

// Demo setup: create the database if it doesn't exist. Production would use EF migrations.
using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();

app.UseStaticFiles();
app.UseRouting();

app.MapRazorPages();
app.MapControllers();
app.MapHub<CallsHub>("/hubs/calls");

app.Run();
