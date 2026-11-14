namespace PersonalOrderDesk.Android;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
		UserAppTheme=AppTheme.Light;
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new MainPage()) { Title = "个人接单工作台" };
	}
}
