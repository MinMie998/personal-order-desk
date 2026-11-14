using Android.App;
using Android.Content.PM;
using Android.OS;
using AndroidX.Activity;
using Microsoft.Extensions.DependencyInjection;
using PersonalOrderDesk.Client;

namespace PersonalOrderDesk.Android;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        OnBackPressedDispatcher.AddCallback(this,new WorkbenchBackCallback(this));
    }
    private sealed class WorkbenchBackCallback(MainActivity activity):OnBackPressedCallback(true)
    {
        private bool pending;
        public override async void HandleOnBackPressed()
        {
            if(pending)return;pending=true;
            try{
                var navigation=IPlatformApplication.Current?.Services.GetService<ClientBackNavigation>();
                if(navigation is not null&&await navigation.GoBack())return;
                activity.MoveTaskToBack(true);
            }catch(Exception){global::Android.Util.Log.Warn("PersonalOrderDesk","Back navigation failed; workbench kept open.");}
            finally{pending=false;}
        }
    }
}
