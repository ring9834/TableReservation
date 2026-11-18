using Amazon.CDK;

namespace Reservation.Infra;

public static class DemoApp
{
    public const string PermanentStackName = "ReservationDemo-Permanent";
    public const string AppStackName = "ReservationDemo-App";

    public static (PermanentStack Permanent, AppStack App) Build(App app, DemoConfig config)
    {
        ArgumentNullException.ThrowIfNull(app);
        var env = new Amazon.CDK.Environment { Account = DemoConfig.Account, Region = DemoConfig.Region };
        var permanent = new PermanentStack(app, PermanentStackName, new StackProps
        {
            Env = env,
            Description = "Reservation demo: public address, site bucket, secrets, budget (kept when the app is deleted)",
            TerminationProtection = true,
        }, config);
        var appStack = new AppStack(app, AppStackName, new StackProps
        {
            Env = env,
            Description = "Reservation demo: Lightsail API container and RDS PostgreSQL (can be deleted and recreated)",
        }, config);
        Tags.Of(app).Add("Project", DemoConfig.Project);
        return (permanent, appStack);
    }
}
