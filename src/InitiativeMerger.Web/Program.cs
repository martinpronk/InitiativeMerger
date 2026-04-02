using InitiativeMerger.Core.Services;
using InitiativeMerger.Web;

var builder = WebApplication.CreateBuilder(args);

// --- Services registreren ---
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddControllers();

// InitiativeMerger core services (scoped zodat logs per request zijn gebonden)
builder.Services.AddScoped<IAzurePolicyService, AzurePolicyService>();
builder.Services.AddScoped<IConflictResolutionService, ConflictResolutionService>();
builder.Services.AddScoped<IDeploymentService, DeploymentService>();
builder.Services.AddScoped<IInitiativeMergerService, InitiativeMergerService>();

// Logging configuratie
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// Security headers configuratie
builder.Services.AddAntiforgery();

var app = builder.Build();

// --- Middleware pipeline ---
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

// Security headers
app.Use(async (context, next) =>
{
    context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    context.Response.Headers.Append("X-Frame-Options", "DENY");
    context.Response.Headers.Append("X-XSS-Protection", "1; mode=block");
    context.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
    await next();
});

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

app.MapControllers();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
