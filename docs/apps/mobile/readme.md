# CardiTrack Mobile App Documentation

## Overview

The CardiTrack Mobile App is a cross-platform .NET MAUI (Multi-platform App UI) application that enables family members and caregivers to monitor CardiMember health data on-the-go. The app provides real-time notifications, offline support, and seamless integration with device-native features like push notifications and HealthKit.

## Technology Stack

- **.NET 10**: Core framework
- **.NET MAUI**: Cross-platform UI framework
- **XAML**: UI markup language
- **MVVM Pattern**: Model-View-ViewModel architecture
- **Platform APIs**: iOS HealthKit, Android Health Connect
- **SQLite**: Local offline storage
- **Push Notifications**: Firebase Cloud Messaging (FCM) / Apple Push Notification Service (APNS)

## Platform Support

### Supported Platforms

- **iOS**: 16.0+ (target: iOS 18)
- **Android**: API 29+ (Android 10; target: Android 15 / API 35)
- **macOS Catalyst**: 15.0+
- **Windows**: 10.0.17763.0+ (Optional)

> Platform minimums match the [mobile screen specifications](../../execution/ui/mobile/ui_screens_maui_mobile.md) and [MVP 1 user stories](../../execution/ui/mobile/mvp1/user_stories.md).

### Recommended Devices

- **iOS**: iPhone 12 and newer
- **Android**: Devices with 2GB+ RAM
- **Tablets**: iPad, Android tablets with 7+ inch screens

## Project Structure

```
CardiTrack.Mobile/
├── Platforms/
│   ├── Android/
│   │   ├── MainActivity.cs
│   │   ├── MainApplication.cs
│   │   ├── AndroidManifest.xml
│   │   └── Resources/
│   ├── iOS/
│   │   ├── AppDelegate.cs
│   │   ├── Program.cs
│   │   ├── Info.plist
│   │   └── Entitlements.plist
│   ├── MacCatalyst/
│   └── Windows/
├── Views/
│   ├── DashboardPage.xaml
│   ├── CardiMemberListPage.xaml
│   ├── CardiMemberDetailPage.xaml
│   ├── AlertsPage.xaml
│   ├── SettingsPage.xaml
│   └── LoginPage.xaml
├── ViewModels/
│   ├── BaseViewModel.cs
│   ├── DashboardViewModel.cs
│   ├── CardiMemberListViewModel.cs
│   ├── CardiMemberDetailViewModel.cs
│   ├── AlertsViewModel.cs
│   └── SettingsViewModel.cs
├── Models/
│   ├── CardiMember.cs
│   ├── Alert.cs
│   ├── HealthMetric.cs
│   └── DeviceConnection.cs
├── Services/
│   ├── IApiService.cs
│   ├── ApiService.cs
│   ├── IAuthService.cs
│   ├── AuthService.cs
│   ├── INotificationService.cs
│   ├── NotificationService.cs
│   ├── ILocalStorageService.cs
│   └── LocalStorageService.cs
├── Helpers/
│   ├── Constants.cs
│   └── Extensions.cs
├── Resources/
│   ├── Fonts/
│   ├── Images/
│   ├── AppIcon/
│   ├── Splash/
│   └── Raw/
├── App.xaml                         # Application resources
├── App.xaml.cs                      # Application lifecycle
├── AppShell.xaml                    # Shell navigation
├── AppShell.xaml.cs
├── MainPage.xaml                    # Initial page
├── MainPage.xaml.cs
├── MauiProgram.cs                   # App configuration
└── CardiTrack.Mobile.csproj         # Project file
```

## Key Features

### 1. Real-Time Health Dashboard

Monitor CardiMember health metrics in real-time:

- **Today's Metrics**: Steps, distance, heart rate, sleep
- **Trend Charts**: Weekly and monthly health trends
- **Quick Stats**: At-a-glance health status
- **Device Status**: Connection and sync information
- **Pull-to-Refresh**: Manual data refresh

### 2. Push Notifications

Receive instant alerts about health changes:

- **Alert Notifications**: High-priority health alerts
- **Severity Indicators**: Color-coded alert levels
- **Deep Linking**: Tap notification to view details
- **Notification History**: Review past notifications
- **Customizable Settings**: Configure notification preferences

### 3. Offline Support

Work seamlessly without connectivity:

- **Local Data Cache**: SQLite database for offline access
- **Sync Queue**: Queue actions when offline
- **Auto-Sync**: Automatic sync when connection restored
- **Offline Indicators**: Clear offline/online status
- **Conflict Resolution**: Handle data conflicts intelligently

### 4. Platform-Specific Features

#### iOS Integration

- **HealthKit Access**: Read health data from Apple Health
- **3D Touch**: Quick actions from home screen
- **Face ID/Touch ID**: Biometric authentication
- **Siri Shortcuts**: Voice commands for common tasks
- **Widgets**: Home screen widgets for quick status

#### Android Integration

- **Health Connect**: Integration with Android Health
- **Material Design**: Native Android UI patterns
- **Quick Settings**: Toggle notifications from quick settings
- **App Shortcuts**: Long-press shortcuts
- **Widgets**: Home screen widgets

### 5. Multi-Member Management

Monitor multiple CardiMembers:

- **Member List**: Scrollable list of all members
- **Member Cards**: Summary cards with key metrics
- **Favorites**: Pin frequently accessed members
- **Search & Filter**: Find members quickly
- **Member Switching**: Easy navigation between members

## Application Configuration

### MauiProgram.cs

Application startup and service configuration:

```csharp
using Microsoft.Extensions.Logging;

namespace CardiTrack.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();

        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        // Register services
        builder.Services.AddSingleton<IApiService, ApiService>();
        builder.Services.AddSingleton<IAuthService, AuthService>();
        builder.Services.AddSingleton<INotificationService, NotificationService>();
        builder.Services.AddSingleton<ILocalStorageService, LocalStorageService>();

        // Register ViewModels
        builder.Services.AddTransient<DashboardViewModel>();
        builder.Services.AddTransient<CardiMemberListViewModel>();
        builder.Services.AddTransient<AlertsViewModel>();
        builder.Services.AddTransient<SettingsViewModel>();

        // Register Pages
        builder.Services.AddTransient<DashboardPage>();
        builder.Services.AddTransient<CardiMemberListPage>();
        builder.Services.AddTransient<AlertsPage>();
        builder.Services.AddTransient<SettingsPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
```

### App.xaml.cs

Application lifecycle management:

```csharp
namespace CardiTrack.Mobile;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        MainPage = new AppShell();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = base.CreateWindow(activationState);

        // Set window size for desktop platforms
        window.Width = 400;
        window.Height = 800;

        return window;
    }

    protected override void OnStart()
    {
        // Handle app start
        base.OnStart();
    }

    protected override void OnSleep()
    {
        // Handle app going to background
        base.OnSleep();
    }

    protected override void OnResume()
    {
        // Handle app coming to foreground
        base.OnResume();
    }
}
```

### AppShell.xaml

Shell-based navigation structure:

```xml
<?xml version="1.0" encoding="UTF-8" ?>
<Shell
    x:Class="CardiTrack.Mobile.AppShell"
    xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
    xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"
    xmlns:local="clr-namespace:CardiTrack.Mobile.Views"
    Title="CardiTrack">

    <TabBar>
        <ShellContent
            Title="Dashboard"
            Icon="dashboard.png"
            ContentTemplate="{DataTemplate local:DashboardPage}"
            Route="dashboard" />

        <ShellContent
            Title="Members"
            Icon="members.png"
            ContentTemplate="{DataTemplate local:CardiMemberListPage}"
            Route="members" />

        <ShellContent
            Title="Alerts"
            Icon="alerts.png"
            ContentTemplate="{DataTemplate local:AlertsPage}"
            Route="alerts" />

        <ShellContent
            Title="Settings"
            Icon="settings.png"
            ContentTemplate="{DataTemplate local:SettingsPage}"
            Route="settings" />
    </TabBar>

</Shell>
```

## MVVM Architecture

### Base ViewModel

```csharp
public class BaseViewModel : INotifyPropertyChanged
{
    private bool _isBusy;
    private string _title;

    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    protected bool SetProperty<T>(ref T backingStore, T value,
        [CallerMemberName] string propertyName = "",
        Action? onChanged = null)
    {
        if (EqualityComparer<T>.Default.Equals(backingStore, value))
            return false;

        backingStore = value;
        onChanged?.Invoke();
        OnPropertyChanged(propertyName);
        return true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string propertyName = "")
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
```

### Example ViewModel

```csharp
public class DashboardViewModel : BaseViewModel
{
    private readonly IApiService _apiService;
    private readonly INotificationService _notificationService;

    private CardiMember _selectedMember;
    private HealthMetrics _todayMetrics;
    private ObservableCollection<Alert> _recentAlerts;

    public DashboardViewModel(IApiService apiService, INotificationService notificationService)
    {
        _apiService = apiService;
        _notificationService = notificationService;

        Title = "Dashboard";
        RecentAlerts = new ObservableCollection<Alert>();

        LoadDataCommand = new Command(async () => await LoadDataAsync());
        RefreshCommand = new Command(async () => await RefreshAsync());
        AcknowledgeAlertCommand = new Command<Alert>(async (alert) => await AcknowledgeAlertAsync(alert));
    }

    public CardiMember SelectedMember
    {
        get => _selectedMember;
        set => SetProperty(ref _selectedMember, value);
    }

    public HealthMetrics TodayMetrics
    {
        get => _todayMetrics;
        set => SetProperty(ref _todayMetrics, value);
    }

    public ObservableCollection<Alert> RecentAlerts
    {
        get => _recentAlerts;
        set => SetProperty(ref _recentAlerts, value);
    }

    public ICommand LoadDataCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand AcknowledgeAlertCommand { get; }

    private async Task LoadDataAsync()
    {
        if (IsBusy) return;

        try
        {
            IsBusy = true;

            // Load today's metrics
            TodayMetrics = await _apiService.GetTodayMetricsAsync(SelectedMember.Id);

            // Load recent alerts
            var alerts = await _apiService.GetRecentAlertsAsync(SelectedMember.Id);
            RecentAlerts.Clear();
            foreach (var alert in alerts)
            {
                RecentAlerts.Add(alert);
            }
        }
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlert("Error", $"Unable to load data: {ex.Message}", "OK");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshAsync()
    {
        await LoadDataAsync();
    }

    private async Task AcknowledgeAlertAsync(Alert alert)
    {
        try
        {
            await _apiService.AcknowledgeAlertAsync(alert.Id);
            RecentAlerts.Remove(alert);
        }
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlert("Error", $"Unable to acknowledge alert: {ex.Message}", "OK");
        }
    }
}
```

### Example View

```xml
<?xml version="1.0" encoding="utf-8" ?>
<ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
             xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"
             xmlns:viewmodels="clr-namespace:CardiTrack.Mobile.ViewModels"
             x:Class="CardiTrack.Mobile.Views.DashboardPage"
             x:DataType="viewmodels:DashboardViewModel"
             Title="{Binding Title}">

    <RefreshView IsRefreshing="{Binding IsBusy}"
                 Command="{Binding RefreshCommand}">
        <ScrollView>
            <VerticalStackLayout Padding="20" Spacing="20">

                <!-- Member Header -->
                <Frame>
                    <Grid ColumnDefinitions="Auto,*" ColumnSpacing="10">
                        <Image Source="member_avatar.png"
                               WidthRequest="50"
                               HeightRequest="50"
                               Aspect="AspectFill" />
                        <VerticalStackLayout Grid.Column="1" VerticalOptions="Center">
                            <Label Text="{Binding SelectedMember.FullName}"
                                   FontSize="18"
                                   FontAttributes="Bold" />
                            <Label Text="{Binding SelectedMember.Age, StringFormat='Age: {0}'}"
                                   FontSize="14"
                                   TextColor="Gray" />
                        </VerticalStackLayout>
                    </Grid>
                </Frame>

                <!-- Today's Metrics -->
                <Label Text="Today's Activity"
                       FontSize="20"
                       FontAttributes="Bold" />

                <Grid ColumnDefinitions="*,*" RowDefinitions="Auto,Auto" ColumnSpacing="10" RowSpacing="10">
                    <Frame Grid.Column="0" Grid.Row="0">
                        <VerticalStackLayout>
                            <Label Text="Steps" FontSize="12" TextColor="Gray" />
                            <Label Text="{Binding TodayMetrics.Steps, StringFormat='{0:N0}'}"
                                   FontSize="24"
                                   FontAttributes="Bold" />
                        </VerticalStackLayout>
                    </Frame>

                    <Frame Grid.Column="1" Grid.Row="0">
                        <VerticalStackLayout>
                            <Label Text="Heart Rate" FontSize="12" TextColor="Gray" />
                            <Label Text="{Binding TodayMetrics.HeartRate, StringFormat='{0} bpm'}"
                                   FontSize="24"
                                   FontAttributes="Bold" />
                        </VerticalStackLayout>
                    </Frame>

                    <Frame Grid.Column="0" Grid.Row="1">
                        <VerticalStackLayout>
                            <Label Text="Distance" FontSize="12" TextColor="Gray" />
                            <Label Text="{Binding TodayMetrics.Distance, StringFormat='{0:F1} mi'}"
                                   FontSize="24"
                                   FontAttributes="Bold" />
                        </VerticalStackLayout>
                    </Frame>

                    <Frame Grid.Column="1" Grid.Row="1">
                        <VerticalStackLayout>
                            <Label Text="Sleep" FontSize="12" TextColor="Gray" />
                            <Label Text="{Binding TodayMetrics.SleepHours, StringFormat='{0:F1} hrs'}"
                                   FontSize="24"
                                   FontAttributes="Bold" />
                        </VerticalStackLayout>
                    </Frame>
                </Grid>

                <!-- Recent Alerts -->
                <Label Text="Recent Alerts"
                       FontSize="20"
                       FontAttributes="Bold" />

                <CollectionView ItemsSource="{Binding RecentAlerts}">
                    <CollectionView.ItemTemplate>
                        <DataTemplate>
                            <Frame Padding="10" Margin="0,5">
                                <Grid ColumnDefinitions="*,Auto">
                                    <VerticalStackLayout>
                                        <Label Text="{Binding Title}"
                                               FontAttributes="Bold" />
                                        <Label Text="{Binding Message}"
                                               FontSize="12"
                                               TextColor="Gray" />
                                        <Label Text="{Binding TriggeredDate, StringFormat='{0:MMM dd, h:mm tt}'}"
                                               FontSize="10"
                                               TextColor="Gray" />
                                    </VerticalStackLayout>
                                    <Button Grid.Column="1"
                                            Text="Ack"
                                            Command="{Binding Source={RelativeSource AncestorType={x:Type viewmodels:DashboardViewModel}}, Path=AcknowledgeAlertCommand}"
                                            CommandParameter="{Binding .}" />
                                </Grid>
                            </Frame>
                        </DataTemplate>
                    </CollectionView.ItemTemplate>
                </CollectionView>

            </VerticalStackLayout>
        </ScrollView>
    </RefreshView>

    <ActivityIndicator IsVisible="{Binding IsBusy}"
                       IsRunning="{Binding IsBusy}"
                       HorizontalOptions="Center"
                       VerticalOptions="Center" />

</ContentPage>
```

## Platform-Specific Implementations

### iOS - Push Notifications

#### Info.plist Configuration

```xml
<key>UIBackgroundModes</key>
<array>
    <string>remote-notification</string>
</array>
```

#### AppDelegate.cs

```csharp
[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    public override bool FinishedLaunching(UIApplication application, NSDictionary launchOptions)
    {
        // Register for push notifications
        UNUserNotificationCenter.Current.RequestAuthorization(
            UNAuthorizationOptions.Alert | UNAuthorizationOptions.Badge | UNAuthorizationOptions.Sound,
            (granted, error) =>
            {
                if (granted)
                {
                    InvokeOnMainThread(() =>
                    {
                        UIApplication.SharedApplication.RegisterForRemoteNotifications();
                    });
                }
            });

        return base.FinishedLaunching(application, launchOptions);
    }

    public override void RegisteredForRemoteNotifications(UIApplication application, NSData deviceToken)
    {
        // Send device token to server
        var token = deviceToken.Description
            .Replace("<", "").Replace(">", "").Replace(" ", "");

        // Store token or send to backend
        Preferences.Set("DeviceToken", token);
    }

    public override void FailedToRegisterForRemoteNotifications(UIApplication application, NSError error)
    {
        Console.WriteLine($"Failed to register for remote notifications: {error.LocalizedDescription}");
    }
}
```

### iOS - HealthKit Integration

#### Entitlements.plist

```xml
<key>com.apple.developer.healthkit</key>
<true/>
<key>com.apple.developer.healthkit.access</key>
<array>
    <string>health-records</string>
</array>
```

#### Info.plist

```xml
<key>NSHealthShareUsageDescription</key>
<string>CardiTrack needs access to read your health data</string>
<key>NSHealthUpdateUsageDescription</key>
<string>CardiTrack needs access to update your health data</string>
```

#### HealthKit Service

```csharp
#if IOS
using HealthKit;

public class HealthKitService : IHealthKitService
{
    private HKHealthStore healthStore;

    public HealthKitService()
    {
        healthStore = new HKHealthStore();
    }

    public async Task<bool> RequestAuthorizationAsync()
    {
        var typesToRead = new NSSet(
            HKObjectType.GetQuantityType(HKQuantityTypeIdentifier.StepCount),
            HKObjectType.GetQuantityType(HKQuantityTypeIdentifier.HeartRate),
            HKObjectType.GetQuantityType(HKQuantityTypeIdentifier.DistanceWalkingRunning)
        );

        var (success, error) = await healthStore.RequestAuthorizationToShareAsync(new NSSet(), typesToRead);
        return success;
    }

    public async Task<int> GetTodayStepsAsync()
    {
        var stepsType = HKQuantityType.GetQuantityType(HKQuantityTypeIdentifier.StepCount);
        var startOfDay = NSDate.FromTimeIntervalSinceNow(-86400); // 24 hours ago
        var now = NSDate.Now;

        var predicate = HKQuery.GetPredicateForSamples(startOfDay, now, HKQueryOptions.StrictStartDate);

        var query = new HKStatisticsQuery(stepsType, predicate, HKStatisticsOptions.CumulativeSum,
            (query, result, error) =>
            {
                if (error != null)
                {
                    Console.WriteLine($"Error querying steps: {error.LocalizedDescription}");
                    return;
                }

                var steps = result?.SumQuantity()?.GetDoubleValue(HKUnit.Count) ?? 0;
                // Return steps
            });

        healthStore.ExecuteQuery(query);
        return 0; // Async handling needed
    }
}
#endif
```

### Android - Push Notifications

#### AndroidManifest.xml

```xml
<manifest xmlns:android="http://schemas.android.com/apk/res/android">
    <application>
        <receiver android:name="com.google.firebase.iid.FirebaseInstanceIdInternalReceiver"
                  android:exported="false" />
        <receiver android:name="com.google.firebase.iid.FirebaseInstanceIdReceiver"
                  android:exported="true"
                  android:permission="com.google.android.c2dm.permission.SEND">
            <intent-filter>
                <action android:name="com.google.android.c2dm.intent.RECEIVE" />
            </intent-filter>
        </receiver>
    </application>

    <uses-permission android:name="android.permission.INTERNET" />
    <uses-permission android:name="android.permission.ACCESS_NETWORK_STATE" />
</manifest>
```

#### MainActivity.cs

```csharp
[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Create notification channel for Android 8.0+
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            CreateNotificationChannel();
        }
    }

    private void CreateNotificationChannel()
    {
        var channelId = "carditrack_alerts";
        var channelName = "Health Alerts";
        var channelDescription = "Receive health alerts for CardiMembers";
        var importance = NotificationImportance.High;

        var channel = new NotificationChannel(channelId, channelName, importance)
        {
            Description = channelDescription
        };

        var notificationManager = GetSystemService(NotificationService) as NotificationManager;
        notificationManager?.CreateNotificationChannel(channel);
    }
}
```

### Android - Health Connect Integration

```csharp
#if ANDROID
using AndroidX.Health.Connect.Client;

public class HealthConnectService : IHealthConnectService
{
    private readonly HealthConnectClient _client;

    public HealthConnectService()
    {
        _client = HealthConnectClient.GetOrCreate(Android.App.Application.Context);
    }

    public async Task<bool> RequestPermissionsAsync()
    {
        var permissions = new[]
        {
            HealthPermission.GetReadPermission(StepsRecord.class),
            HealthPermission.GetReadPermission(HeartRateRecord.class),
            HealthPermission.GetReadPermission(DistanceRecord.class)
        };

        // Request permissions
        // Implementation depends on Health Connect SDK version
        return true;
    }

    public async Task<int> GetTodayStepsAsync()
    {
        var startTime = DateTime.Today;
        var endTime = DateTime.Now;

        var request = new ReadRecordsRequest.Builder<StepsRecord>()
            .SetTimeRangeFilter(startTime, endTime)
            .Build();

        var response = await _client.ReadRecordsAsync(request);
        var totalSteps = response.Records.Sum(r => r.Count);
        return totalSteps;
    }
}
#endif
```

## Services Implementation

### API Service

```csharp
public class ApiService : IApiService
{
    private readonly HttpClient _httpClient;
    private readonly IAuthService _authService;
    private const string BaseUrl = "https://api.carditrack.com";

    public ApiService(IAuthService authService)
    {
        _authService = authService;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(BaseUrl)
        };
    }

    private async Task<HttpClient> GetAuthenticatedClientAsync()
    {
        var token = await _authService.GetAccessTokenAsync();
        _httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return _httpClient;
    }

    public async Task<List<CardiMember>> GetCardiMembersAsync()
    {
        var client = await GetAuthenticatedClientAsync();
        var response = await client.GetAsync("/api/cardimembers");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<List<CardiMember>>(json);
    }

    public async Task<HealthMetrics> GetTodayMetricsAsync(Guid cardiMemberId)
    {
        var client = await GetAuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/dashboard/{cardiMemberId}/today");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<HealthMetrics>(json);
    }

    public async Task<List<Alert>> GetRecentAlertsAsync(Guid cardiMemberId, int count = 10)
    {
        var client = await GetAuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/alerts/{cardiMemberId}?limit={count}");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<List<Alert>>(json);
    }

    public async Task AcknowledgeAlertAsync(Guid alertId)
    {
        var client = await GetAuthenticatedClientAsync();
        var response = await client.PostAsync($"/api/alerts/{alertId}/acknowledge", null);
        response.EnsureSuccessStatusCode();
    }
}
```

### Local Storage Service

```csharp
public class LocalStorageService : ILocalStorageService
{
    private readonly SQLiteAsyncConnection _database;
    private const string DatabaseFilename = "carditrack.db3";

    public LocalStorageService()
    {
        var dbPath = Path.Combine(FileSystem.AppDataDirectory, DatabaseFilename);
        _database = new SQLiteAsyncConnection(dbPath);

        // Create tables
        _database.CreateTableAsync<CardiMember>().Wait();
        _database.CreateTableAsync<Alert>().Wait();
        _database.CreateTableAsync<HealthMetric>().Wait();
    }

    public async Task<List<CardiMember>> GetCardiMembersAsync()
    {
        return await _database.Table<CardiMember>().ToListAsync();
    }

    public async Task SaveCardiMemberAsync(CardiMember member)
    {
        var existing = await _database.Table<CardiMember>()
            .Where(m => m.Id == member.Id)
            .FirstOrDefaultAsync();

        if (existing != null)
            await _database.UpdateAsync(member);
        else
            await _database.InsertAsync(member);
    }

    public async Task<List<Alert>> GetAlertsAsync(Guid cardiMemberId)
    {
        return await _database.Table<Alert>()
            .Where(a => a.CardiMemberId == cardiMemberId)
            .OrderByDescending(a => a.TriggeredDate)
            .ToListAsync();
    }

    public async Task SaveAlertAsync(Alert alert)
    {
        await _database.InsertOrReplaceAsync(alert);
    }

    public async Task DeleteAlertAsync(Guid alertId)
    {
        await _database.DeleteAsync<Alert>(alertId);
    }

    public async Task ClearAllDataAsync()
    {
        await _database.DeleteAllAsync<CardiMember>();
        await _database.DeleteAllAsync<Alert>();
        await _database.DeleteAllAsync<HealthMetric>();
    }
}
```

### Notification Service

```csharp
public class NotificationService : INotificationService
{
    public async Task RegisterForNotificationsAsync()
    {
#if IOS
        await RequestiOSPermissionsAsync();
#elif ANDROID
        await RequestAndroidPermissionsAsync();
#endif
    }

    public async Task ShowLocalNotificationAsync(string title, string message)
    {
        var notification = new NotificationRequest
        {
            NotificationId = new Random().Next(),
            Title = title,
            Description = message,
            Schedule = new NotificationRequestSchedule
            {
                NotifyTime = DateTime.Now.AddSeconds(1)
            }
        };

        await LocalNotificationCenter.Current.Show(notification);
    }

    public async Task<string> GetDeviceTokenAsync()
    {
        return await Task.FromResult(Preferences.Get("DeviceToken", string.Empty));
    }

#if IOS
    private async Task RequestiOSPermissionsAsync()
    {
        var (granted, error) = await UNUserNotificationCenter.Current
            .RequestAuthorizationAsync(
                UNAuthorizationOptions.Alert |
                UNAuthorizationOptions.Badge |
                UNAuthorizationOptions.Sound);

        if (granted)
        {
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                UIApplication.SharedApplication.RegisterForRemoteNotifications();
            });
        }
    }
#endif

#if ANDROID
    private async Task RequestAndroidPermissionsAsync()
    {
        // Android 13+ requires explicit notification permission
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
        {
            var status = await Permissions.RequestAsync<Permissions.PostNotifications>();
            // Handle permission result
        }
    }
#endif
}
```

## Building and Deploying

### Prerequisites

- .NET 10 SDK
- Visual Studio 2025 or VS Code with .NET MAUI workload
- Xcode 15+ (for iOS development)
- Android SDK 34+ (for Android development)

### Install .NET MAUI Workload

```bash
dotnet workload install maui
```

### Build for Development

```bash
# Navigate to mobile project
cd C:\Code\Github\Carditrack\src\Presentation\CardiTrack.Mobile

# Restore dependencies
dotnet restore

# Build for Android
dotnet build -f net10.0-android

# Build for iOS (Mac only)
dotnet build -f net10.0-ios

# Build for all platforms
dotnet build
```

### Run on Emulator/Simulator

```bash
# Run on Android emulator
dotnet run -f net10.0-android

# Run on iOS simulator (Mac only)
dotnet run -f net10.0-ios
```

### Deploy to Physical Devices

#### iOS

```bash
# Build for device
dotnet build -f net10.0-ios -c Release

# Deploy to connected device
dotnet build -f net10.0-ios -c Release -p:RuntimeIdentifier=ios-arm64 -t:Run
```

#### Android

```bash
# Build APK
dotnet publish -f net10.0-android -c Release

# Deploy to connected device
adb install bin/Release/net10.0-android/publish/com.carditrack.mobile-Signed.apk
```

### Publishing to App Stores

#### iOS App Store

1. **Configure signing**:
   ```xml
   <PropertyGroup Condition="'$(Configuration)' == 'Release'">
     <CodesignKey>iPhone Distribution</CodesignKey>
     <CodesignProvision>CardiTrack Distribution</CodesignProvision>
   </PropertyGroup>
   ```

2. **Build archive**:
   ```bash
   dotnet publish -f net10.0-ios -c Release
   ```

3. **Upload to App Store Connect**:
   - Use Xcode or Transporter app
   - Submit for review

#### Google Play Store

1. **Generate signed APK**:
   ```bash
   dotnet publish -f net10.0-android -c Release -p:AndroidKeyStore=true \
     -p:AndroidSigningKeyStore=carditrack.keystore \
     -p:AndroidSigningKeyAlias=carditrack \
     -p:AndroidSigningKeyPass=<password> \
     -p:AndroidSigningStorePass=<password>
   ```

2. **Generate AAB (Android App Bundle)**:
   ```bash
   dotnet publish -f net10.0-android -c Release -p:AndroidPackageFormat=aab
   ```

3. **Upload to Play Console**:
   - Go to Google Play Console
   - Upload AAB file
   - Submit for review

### CI/CD Pipeline

#### GitHub Actions for Mobile

```yaml
name: Build Mobile Apps

on:
  push:
    branches: [main]
    paths:
      - 'src/Presentation/CardiTrack.Mobile/**'

jobs:
  build-android:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v3

      - name: Setup .NET
        uses: actions/setup-dotnet@v3
        with:
          dotnet-version: '10.0.x'

      - name: Install MAUI workload
        run: dotnet workload install maui

      - name: Build Android
        run: dotnet publish src/Presentation/CardiTrack.Mobile -f net10.0-android -c Release

      - name: Upload APK
        uses: actions/upload-artifact@v3
        with:
          name: android-apk
          path: src/Presentation/CardiTrack.Mobile/bin/Release/net10.0-android/publish/*.apk

  build-ios:
    runs-on: macos-latest
    steps:
      - uses: actions/checkout@v3

      - name: Setup .NET
        uses: actions/setup-dotnet@v3
        with:
          dotnet-version: '10.0.x'

      - name: Install MAUI workload
        run: dotnet workload install maui

      - name: Build iOS
        run: dotnet build src/Presentation/CardiTrack.Mobile -f net10.0-ios -c Release
```

## Testing

### Unit Testing ViewModels

```csharp
public class DashboardViewModelTests
{
    private readonly Mock<IApiService> _mockApiService;
    private readonly Mock<INotificationService> _mockNotificationService;
    private readonly DashboardViewModel _viewModel;

    public DashboardViewModelTests()
    {
        _mockApiService = new Mock<IApiService>();
        _mockNotificationService = new Mock<INotificationService>();
        _viewModel = new DashboardViewModel(_mockApiService.Object, _mockNotificationService.Object);
    }

    [Fact]
    public async Task LoadData_Success_UpdatesMetrics()
    {
        // Arrange
        var metrics = new HealthMetrics { Steps = 5000, HeartRate = 70 };
        _mockApiService.Setup(x => x.GetTodayMetricsAsync(It.IsAny<Guid>()))
            .ReturnsAsync(metrics);

        // Act
        await _viewModel.LoadDataCommand.ExecuteAsync(null);

        // Assert
        Assert.Equal(5000, _viewModel.TodayMetrics.Steps);
        Assert.Equal(70, _viewModel.TodayMetrics.HeartRate);
    }
}
```

### UI Testing

```csharp
[TestClass]
public class AppTests : UITest
{
    [TestMethod]
    public void LoginPage_ValidCredentials_NavigatesToDashboard()
    {
        // Arrange
        App.EnterText("EmailEntry", "test@example.com");
        App.EnterText("PasswordEntry", "password123");

        // Act
        App.Tap("LoginButton");

        // Assert
        App.WaitForElement("DashboardPage");
    }
}
```

## Troubleshooting

### Common Issues

**Issue: Build fails on iOS**
```bash
# Clean build artifacts
dotnet clean
rm -rf bin obj

# Rebuild
dotnet build -f net10.0-ios
```

**Issue: Android emulator not detected**
```bash
# List available devices
adb devices

# Start emulator
emulator -avd Pixel_5_API_34
```

**Issue: App crashes on startup**
- Check Application Output window for exceptions
- Verify all NuGet packages are restored
- Ensure platform-specific code has proper conditionals

**Issue: Push notifications not working**
- Verify Firebase/APNS configuration
- Check device token registration
- Confirm notification permissions granted

## Performance Optimization

1. **Use compiled bindings**: Add `x:DataType` to improve performance
2. **Virtualize collections**: Use `CollectionView` with virtualization
3. **Optimize images**: Use compressed formats, appropriate sizes
4. **Lazy loading**: Load views and data on demand
5. **Background tasks**: Use background services for sync operations
6. **Caching**: Cache API responses locally
7. **Reduce app size**: Enable linking and AOT compilation

## Best Practices

1. **MVVM Pattern**: Separate business logic from UI
2. **Dependency Injection**: Use DI for loose coupling
3. **Async/Await**: Use async operations for responsiveness
4. **Error Handling**: Implement comprehensive error handling
5. **Offline Support**: Design for intermittent connectivity
6. **Security**: Never store sensitive data in plain text
7. **Accessibility**: Support screen readers and font scaling
8. **Localization**: Support multiple languages and cultures

## Related Documentation

- [Web Dashboard Documentation](../web/readme.md)
- [API Documentation](../api/readme.md)
- [Infrastructure Guide](../../infrastructure.md)
- [.NET MAUI Official Docs](https://learn.microsoft.com/dotnet/maui/)

## Support

For mobile app issues, contact: mobile-support@carditrack.com
