# CardiTrack Web Dashboard Documentation

## Overview

The CardiTrack Web Dashboard is a Blazor Server application that provides real-time health monitoring and alert management for family members and caregivers. It offers an interactive, responsive interface for viewing CardiMember health data, managing alerts, and monitoring multiple family members from a single dashboard.

## Technology Stack

- **.NET 10**: Core framework
- **Blazor Server**: Interactive server-side rendering with SignalR
- **Bootstrap 5**: UI component framework
- **SignalR**: Real-time communication
- **Razor Components**: Component-based UI architecture
- **Interactive Server Components**: Server-side interactivity

## Project Structure

```
CardiTrack.Web/
├── Components/
│   ├── App.razor                    # Root application component
│   ├── Routes.razor                 # Routing configuration
│   ├── _Imports.razor              # Global using directives
│   ├── Layout/
│   │   ├── MainLayout.razor        # Main application layout
│   │   ├── NavMenu.razor           # Navigation sidebar
│   │   └── ReconnectModal.razor    # SignalR reconnection UI
│   └── Pages/
│       ├── Home.razor              # Dashboard home page
│       ├── Counter.razor           # Example counter page
│       ├── Weather.razor           # Example weather page
│       ├── Error.razor             # Error handling page
│       └── NotFound.razor          # 404 page
├── wwwroot/
│   ├── css/
│   ├── js/
│   └── favicon.png
├── Program.cs                       # Application entry point
├── appsettings.json                # Configuration
└── CardiTrack.Web.csproj           # Project file
```

## Key Features

### 1. Real-Time Health Monitoring

The dashboard provides live updates of CardiMember health data using SignalR:

- **Real-time metrics**: Steps, heart rate, sleep quality
- **Activity trends**: Weekly and monthly patterns
- **Device status**: Connection state and last sync time
- **Alert notifications**: Instant alerts when health patterns deviate

### 2. Alert Management

Comprehensive alert handling and tracking:

- **Alert severity levels**: Green, Yellow, Orange, Red
- **Alert acknowledgment**: Mark alerts as reviewed
- **Alert history**: View past alerts and patterns
- **Alert filtering**: By severity, date, and status
- **Alert details**: Detailed metric values and context

### 3. Multi-Member Support

Monitor multiple CardiMembers from a single interface:

- **Family dashboard**: Overview of all CardiMembers
- **Member profiles**: Detailed view for each member
- **Role-based access**: Admin, Staff, Viewer roles
- **Member comparison**: Side-by-side health metrics
- **Organization management**: Multi-tenant support

### 4. Device Management

Manage wearable device connections:

- **Device onboarding**: OAuth flow integration
- **Connection status**: Real-time sync status
- **Multi-device support**: Fitbit, Apple Watch, Garmin
- **Token management**: Automatic refresh handling
- **Sync history**: Last sync timestamps

## Core Components

### MainLayout.razor

The primary layout component that wraps all pages:

```razor
@inherits LayoutComponentBase

<div class="page">
    <div class="sidebar">
        <NavMenu />
    </div>
    <main>
        <div class="top-row px-4">
            <a href="https://learn.microsoft.com/aspnet/core/" target="_blank">About</a>
        </div>
        <article class="content px-4">
            @Body
        </article>
    </main>
</div>
<ReconnectModal />
```

### NavMenu.razor

Navigation sidebar with route links:

```razor
<div class="top-row ps-3 navbar navbar-dark">
    <div class="container-fluid">
        <a class="navbar-brand" href="">CardiTrack.Web</a>
    </div>
</div>

<nav class="nav flex-column">
    <div class="nav-item px-3">
        <NavLink class="nav-link" href="" Match="NavLinkMatch.All">
            <span class="bi bi-house-door-fill" aria-hidden="true"></span> Home
        </NavLink>
    </div>
    <div class="nav-item px-3">
        <NavLink class="nav-link" href="counter">
            <span class="bi bi-plus-square-fill" aria-hidden="true"></span> Counter
        </NavLink>
    </div>
    <div class="nav-item px-3">
        <NavLink class="nav-link" href="weather">
            <span class="bi bi-list-nested" aria-hidden="true"></span> Weather
        </NavLink>
    </div>
</nav>
```

### ReconnectModal.razor

Handles SignalR reconnection scenarios:

```razor
<div id="reconnect-modal" class="modal" style="display: none;">
    <div class="modal-dialog">
        <div class="modal-content">
            <div class="modal-header">
                <h5 class="modal-title">Connection Lost</h5>
            </div>
            <div class="modal-body">
                <p>Attempting to reconnect to the server...</p>
                <div class="spinner-border" role="status">
                    <span class="visually-hidden">Loading...</span>
                </div>
            </div>
        </div>
    </div>
</div>
```

## Application Startup

### Program.cs Configuration

```csharp
using CardiTrack.Web.Components;

var builder = WebApplication.CreateBuilder(args);

// Add Razor Components with Interactive Server support
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Configure HTTP request pipeline
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

// Status code pages for 404, 500, etc.
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseHttpsRedirection();
app.UseAntiforgery();
app.MapStaticAssets();

// Map Razor Components with Interactive Server render mode
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
```

### Project Dependencies

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <BlazorDisableThrowNavigationException>true</BlazorDisableThrowNavigationException>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\Core\CardiTrack.Application\CardiTrack.Application.csproj" />
    <ProjectReference Include="..\..\Infrastructure\CardiTrack.Infrastructure\CardiTrack.Infrastructure.csproj" />
  </ItemGroup>
</Project>
```

## SignalR Integration

### Real-Time Updates

Blazor Server uses SignalR automatically for component updates:

```razor
@page "/dashboard/{cardiMemberId:guid}"
@inject ICardiMemberService CardiMemberService
@implements IAsyncDisposable

<h3>CardiMember Dashboard</h3>

<div class="metrics">
    <div class="metric-card">
        <h4>Steps Today</h4>
        <p class="metric-value">@currentSteps</p>
    </div>
    <div class="metric-card">
        <h4>Heart Rate</h4>
        <p class="metric-value">@currentHeartRate bpm</p>
    </div>
</div>

@code {
    [Parameter]
    public Guid CardiMemberId { get; set; }

    private int currentSteps;
    private int currentHeartRate;
    private Timer? timer;

    protected override async Task OnInitializedAsync()
    {
        await LoadData();

        // Refresh data every 30 seconds
        timer = new Timer(async _ =>
        {
            await LoadData();
            await InvokeAsync(StateHasChanged);
        }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    private async Task LoadData()
    {
        var data = await CardiMemberService.GetTodayMetrics(CardiMemberId);
        currentSteps = data.Steps;
        currentHeartRate = data.RestingHeartRate;
    }

    public async ValueTask DisposeAsync()
    {
        if (timer != null)
            await timer.DisposeAsync();
    }
}
```

### Hub Configuration

For custom real-time features, configure SignalR hubs:

```csharp
// In Program.cs
builder.Services.AddSignalR();

// Map hub endpoints
app.MapHub<AlertHub>("/alertHub");
app.MapHub<HealthDataHub>("/healthHub");
```

### Hub Implementation

```csharp
public class AlertHub : Hub
{
    private readonly IAlertService _alertService;

    public AlertHub(IAlertService alertService)
    {
        _alertService = alertService;
    }

    public async Task SubscribeToCardiMember(Guid cardiMemberId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"cardimember-{cardiMemberId}");
    }

    public async Task UnsubscribeFromCardiMember(Guid cardiMemberId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"cardimember-{cardiMemberId}");
    }
}
```

### Client-Side Hub Connection

```razor
@inject IJSRuntime JS
@implements IAsyncDisposable

@code {
    private HubConnection? hubConnection;

    protected override async Task OnInitializedAsync()
    {
        hubConnection = new HubConnectionBuilder()
            .WithUrl(NavigationManager.ToAbsoluteUri("/alertHub"))
            .Build();

        hubConnection.On<Alert>("ReceiveAlert", async (alert) =>
        {
            // Handle incoming alert
            alerts.Add(alert);
            await InvokeAsync(StateHasChanged);
        });

        await hubConnection.StartAsync();
        await hubConnection.InvokeAsync("SubscribeToCardiMember", CardiMemberId);
    }

    public async ValueTask DisposeAsync()
    {
        if (hubConnection is not null)
        {
            await hubConnection.DisposeAsync();
        }
    }
}
```

## Authentication Flow

### Auth0 Integration

The web dashboard integrates with Auth0 for authentication:

```csharp
// In Program.cs
builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = "Auth0";
})
.AddCookie()
.AddAuth0WebAppAuthentication(options =>
{
    options.Domain = builder.Configuration["Auth0:Domain"];
    options.ClientId = builder.Configuration["Auth0:ClientId"];
});

builder.Services.AddAuthorization();
```

### Protected Pages

```razor
@page "/dashboard"
@attribute [Authorize]
@using Microsoft.AspNetCore.Authorization

<AuthorizeView>
    <Authorized>
        <h3>Welcome, @context.User.Identity?.Name!</h3>
        <!-- Dashboard content -->
    </Authorized>
    <NotAuthorized>
        <p>You must log in to access this page.</p>
    </NotAuthorized>
</AuthorizeView>
```

### Login Component

```razor
@page "/login"
@inject NavigationManager NavigationManager

<div class="login-container">
    <h2>Sign In to CardiTrack</h2>
    <button class="btn btn-primary" @onclick="Login">
        Sign In with Auth0
    </button>
</div>

@code {
    private void Login()
    {
        NavigationManager.NavigateTo("/Account/Login", forceLoad: true);
    }
}
```

### User Context

Access current user information:

```razor
@inject IHttpContextAccessor HttpContextAccessor

@code {
    private string? userId;
    private string? userEmail;

    protected override void OnInitialized()
    {
        var user = HttpContextAccessor.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated == true)
        {
            userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            userEmail = user.FindFirst(ClaimTypes.Email)?.Value;
        }
    }
}
```

## Pages and Routing

### Route Configuration

Routes are defined using the `@page` directive:

```razor
@page "/cardimembers"
@page "/cardimembers/{id:guid}"
@page "/dashboard/{cardiMemberId:guid}/alerts"
```

### Navigation

Navigate between pages programmatically:

```razor
@inject NavigationManager NavigationManager

<button @onclick="NavigateToDashboard">View Dashboard</button>

@code {
    private void NavigateToDashboard()
    {
        NavigationManager.NavigateTo($"/dashboard/{cardiMemberId}");
    }
}
```

### Query Parameters

Access query parameters:

```razor
@page "/alerts"
@inject NavigationManager NavigationManager

@code {
    protected override void OnInitialized()
    {
        var uri = NavigationManager.ToAbsoluteUri(NavigationManager.Uri);
        if (QueryHelpers.ParseQuery(uri.Query).TryGetValue("severity", out var severity))
        {
            selectedSeverity = severity;
        }
    }
}
```

## Configuration

### appsettings.json

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "ConnectionStrings": {
    "DefaultConnection": "Server=localhost;Database=CardiTrack;Trusted_Connection=True;TrustServerCertificate=True;"
  },
  "Auth0": {
    "Domain": "carditrack.auth0.com",
    "ClientId": "YOUR_CLIENT_ID",
    "ClientSecret": "YOUR_CLIENT_SECRET"
  },
  "ApiBaseUrl": "https://localhost:7001",
  "SignalR": {
    "ConnectionString": ""
  }
}
```

### appsettings.Development.json

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Debug",
      "Microsoft.AspNetCore": "Information"
    }
  },
  "DetailedErrors": true,
  "Auth0": {
    "Domain": "carditrack-dev.auth0.com",
    "ClientId": "DEV_CLIENT_ID"
  }
}
```

## Running Locally

### Prerequisites

- .NET 10 SDK
- SQL Server (LocalDB or full installation)
- Visual Studio 2025 or VS Code
- Auth0 account (for authentication)

### Setup Steps

```bash
# 1. Navigate to the Web project
cd C:\Code\Github\Carditrack\src\Presentation\CardiTrack.Web

# 2. Restore dependencies
dotnet restore

# 3. Update database (if using EF Core)
dotnet ef database update --project ../../Infrastructure/CardiTrack.Infrastructure

# 4. Run the application
dotnet run

# 5. Open browser
# Navigate to: https://localhost:5001
```

### Development with Hot Reload

```bash
# Run with hot reload enabled
dotnet watch run

# The application will automatically reload when you make changes
```

### Using Visual Studio

1. Open `CardiTrack.sln`
2. Set `CardiTrack.Web` as the startup project
3. Press F5 to run with debugging
4. Access at `https://localhost:5001`

## Building for Production

### Build Configuration

```bash
# Build in Release mode
dotnet build --configuration Release

# Publish for deployment
dotnet publish --configuration Release --output ./publish
```

### Docker Support

Create a `Dockerfile`:

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["src/Presentation/CardiTrack.Web/CardiTrack.Web.csproj", "CardiTrack.Web/"]
COPY ["src/Core/CardiTrack.Application/CardiTrack.Application.csproj", "CardiTrack.Application/"]
COPY ["src/Infrastructure/CardiTrack.Infrastructure/CardiTrack.Infrastructure.csproj", "CardiTrack.Infrastructure/"]
RUN dotnet restore "CardiTrack.Web/CardiTrack.Web.csproj"
COPY . .
WORKDIR "/src/CardiTrack.Web"
RUN dotnet build "CardiTrack.Web.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "CardiTrack.Web.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "CardiTrack.Web.dll"]
```

Build and run with Docker:

```bash
# Build Docker image
docker build -t carditrack-web .

# Run container
docker run -d -p 8080:80 -p 8443:443 --name carditrack-web carditrack-web
```

## Deployment

### Azure App Service

```bash
# Login to Azure
az login

# Create resource group
az group create --name carditrack-rg --location eastus

# Create App Service plan
az appservice plan create --name carditrack-plan --resource-group carditrack-rg --sku B1

# Create Web App
az webapp create --name carditrack-web --resource-group carditrack-rg --plan carditrack-plan

# Deploy
az webapp deployment source config-zip --resource-group carditrack-rg --name carditrack-web --src ./publish.zip
```

### Environment Variables

Set production configuration in Azure:

```bash
az webapp config appsettings set --resource-group carditrack-rg --name carditrack-web --settings \
  "Auth0__Domain=carditrack.auth0.com" \
  "Auth0__ClientId=PROD_CLIENT_ID" \
  "Auth0__ClientSecret=PROD_CLIENT_SECRET" \
  "ConnectionStrings__DefaultConnection=SERVER_CONNECTION_STRING"
```

## Performance Optimization

### Prerendering

Enable prerendering for faster initial load:

```razor
@rendermode InteractiveServer(prerender: true)
```

### Lazy Loading

Implement lazy loading for large components:

```razor
@code {
    private bool showHeavyComponent = false;

    private async Task LoadHeavyComponent()
    {
        showHeavyComponent = true;
        await Task.Delay(100); // Give UI time to update
    }
}
```

### Response Caching

Enable response caching for static data:

```csharp
// In Program.cs
builder.Services.AddResponseCaching();
app.UseResponseCaching();
```

### SignalR Optimization

Configure SignalR for production:

```csharp
builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = false; // Disable in production
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
});
```

## Troubleshooting

### Common Issues

**Issue: SignalR connection fails**
```
Solution: Check firewall settings and ensure WebSocket support is enabled
```

**Issue: Authentication redirects fail**
```
Solution: Verify Auth0 callback URLs are correctly configured
```

**Issue: CSS not loading**
```bash
# Clear browser cache and rebuild
dotnet clean
dotnet build
```

**Issue: Hot reload not working**
```bash
# Restart with explicit hot reload
dotnet watch run --no-hot-reload
```

### Debugging

Enable detailed errors:

```json
{
  "DetailedErrors": true,
  "Logging": {
    "LogLevel": {
      "Default": "Debug"
    }
  }
}
```

View logs:

```bash
# In development
# Logs appear in console output

# In production (Azure)
az webapp log tail --name carditrack-web --resource-group carditrack-rg
```

## Testing

### Component Testing

Use bUnit for Blazor component testing:

```csharp
public class HomePageTests : TestContext
{
    [Fact]
    public void HomePageRendersCorrectly()
    {
        // Arrange
        var cut = RenderComponent<Home>();

        // Assert
        cut.Find("h1").MarkupMatches("<h1>Hello, world!</h1>");
    }
}
```

### Integration Testing

Test with TestServer:

```csharp
public class WebTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public WebTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task HomePage_ReturnsSuccessStatusCode()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/");

        response.EnsureSuccessStatusCode();
    }
}
```

## Best Practices

1. **Component Organization**: Keep components small and focused
2. **State Management**: Use cascading parameters for shared state
3. **Error Handling**: Implement error boundaries
4. **Loading States**: Show loading indicators during async operations
5. **Accessibility**: Use semantic HTML and ARIA labels
6. **Security**: Validate all user input, use CSRF tokens
7. **Performance**: Virtualize long lists, lazy load components
8. **Testing**: Write unit tests for business logic

## Related Documentation

- [API Documentation](../api/readme.md)
- [Mobile App Documentation](../mobile/readme.md)
- [Infrastructure Guide](../../infrastructure.md)
- [Authentication Setup](../../technical/auth0_integration.md)

## Support

For web dashboard issues, contact: web-support@carditrack.com
