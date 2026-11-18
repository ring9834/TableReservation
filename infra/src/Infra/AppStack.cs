using Amazon.CDK;
using Amazon.CDK.AWS.EC2;
using Amazon.CDK.AWS.Lightsail;
using Amazon.CDK.AWS.RDS;
using Amazon.CDK.AWS.SSM;
using Constructs;

namespace Reservation.Infra;

/// <summary>
/// What can be deleted between rehearsals and recreated (Design §17.4): the Lightsail container service running the API,
/// and RDS PostgreSQL in the default VPC, reachable only from the Lightsail VPC through peering. No NAT gateway, load
/// balancer or Elastic IP. Secrets reach the container as CloudFormation dynamic references, resolved inside AWS.
/// </summary>
public sealed class AppStack : Stack
{
    public const string ServiceName = "reservation-api";
    public const string ContainerName = "api";
    public const int ContainerPort = 8080;
    public const string DatabaseName = "reservations";

    /// <summary>The container's environment variables that carry secrets (each must be a dynamic reference).</summary>
    public static readonly string[] SecretVariables =
    [
        "ConnectionStrings__Reservations",
        "Demo__HostPasscode",
        "Demo__PresenterPasscode",
        "Stripe__SecretKey",
        "Stripe__PublishableKey",
        "Stripe__WebhookSecret",
        "Pos__WebhookSecret",
        "GuestLinks__SigningKey",
        "Edge__OriginSecret",
    ];

    public AppStack(Construct scope, string id, IStackProps props, DemoConfig config)
        : base(scope, id, props)
    {
        ArgumentNullException.ThrowIfNull(config);

        var vpc = Vpc.FromLookup(this, "DefaultVpc", new VpcLookupOptions { IsDefault = true });
        var databaseGroup = new SecurityGroup(this, "DatabaseSecurityGroup", new SecurityGroupProps
        {
            Vpc = vpc,
            Description = "Reservation demo database: PostgreSQL from the Lightsail VPC (peering) only",
            AllowAllOutbound = false,
        });
        databaseGroup.AddIngressRule(Peer.Ipv4(config.LightsailCidr), Port.Tcp(5432), "PostgreSQL from Lightsail (VPC peering)");

        var database = new DatabaseInstance(this, "Database", new DatabaseInstanceProps
        {
            Engine = DatabaseInstanceEngine.Postgres(new PostgresInstanceEngineProps { Version = PostgresEngineVersion.VER_16 }),
            InstanceType = Amazon.CDK.AWS.EC2.InstanceType.Of(InstanceClass.T4G, InstanceSize.MICRO),
            Vpc = vpc,
            // The default VPC has public subnets only; PubliclyAccessible=false gives the instance no public address.
            VpcSubnets = new SubnetSelection { SubnetType = SubnetType.PUBLIC },
            SecurityGroups = [databaseGroup],
            PubliclyAccessible = false,
            MultiAz = false,
            AllocatedStorage = 20,
            StorageType = StorageType.GP3,
            StorageEncrypted = true,
            BackupRetention = Duration.Days(7),
            DeleteAutomatedBackups = true,
            DeletionProtection = false,
            RemovalPolicy = RemovalPolicy.DESTROY, // the demo database is seeded data only; no final snapshot
            DatabaseName = DatabaseName,
            // No fixed secret name: a deleted secret keeps its name for 7+ days, which would block recreating the stack.
            // Characters that would break a connection string are excluded.
            Credentials = Credentials.FromGeneratedSecret("reservations", new CredentialsBaseOptions
            {
                ExcludeCharacters = " %+~`#$&*()|[]{}:;<>?!'/@\"\\=,^",
            }),
            EnablePerformanceInsights = false,
            AutoMinorVersionUpgrade = true,
        });
        database.Secret!.ApplyRemovalPolicy(RemovalPolicy.DESTROY);

        var service = new CfnContainer(this, "Api", new CfnContainerProps
        {
            ServiceName = ServiceName,
            Power = "micro",
            Scale = 1,
            IsDisabled = false,
            ContainerServiceDeployment = config.ImageName is { } image ? Deployment(image, config, database) : null,
        });

        if (config.ImageName is null)
        {
            Annotations.Of(this).AddInfo("No -c ImageName=…: the container service is created without a deployment (push an image first).");
        }

        _ = new CfnOutput(this, "ApiEndpoint", new CfnOutputProps { Value = service.AttrUrl });
        _ = new CfnOutput(this, "DatabaseEndpoint", new CfnOutputProps { Value = database.DbInstanceEndpointAddress });
    }

    private CfnContainer.ContainerServiceDeploymentProperty Deployment(string image, DemoConfig config, DatabaseInstance database)
    {
        var siteDomain = StringParameter.ValueForStringParameter(this, PermanentStack.SiteDomainParameter);
        var dbSecret = database.Secret!;
        var connectionString =
            $"Host={database.DbInstanceEndpointAddress};Port={database.DbInstanceEndpointPort};Database={DatabaseName};" +
            $"Username={dbSecret.SecretValueFromJson("username").UnsafeUnwrap()};" +
            $"Password={dbSecret.SecretValueFromJson("password").UnsafeUnwrap()};" +
            "SSL Mode=Require;Maximum Pool Size=20";

        var environment = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Demo",
            ["GuestLinks__BaseUrl"] = "https://" + siteDomain,
            ["DEPLOY_ID"] = config.DeployId,
            ["ConnectionStrings__Reservations"] = connectionString,
            ["Demo__HostPasscode"] = Operator("HostPasscode"),
            ["Demo__PresenterPasscode"] = Operator("PresenterPasscode"),
            ["Stripe__SecretKey"] = Operator("StripeSecretKey"),
            ["Stripe__PublishableKey"] = Operator("StripePublishableKey"),
            ["Stripe__WebhookSecret"] = Operator("StripeWebhookSecret"),
            ["Pos__WebhookSecret"] = Whole(PermanentStack.PosWebhookSecretName),
            ["GuestLinks__SigningKey"] = Whole(PermanentStack.GuestLinkKeyName),
            ["Edge__OriginSecret"] = Whole(PermanentStack.OriginVerifyName),
        };

        return new CfnContainer.ContainerServiceDeploymentProperty
        {
            Containers = new object[]
            {
                new CfnContainer.ContainerProperty
                {
                    ContainerName = ContainerName,
                    Image = image,
                    Ports = new object[]
                    {
                        new CfnContainer.PortInfoProperty { Port = ContainerPort.ToString(System.Globalization.CultureInfo.InvariantCulture), Protocol = "HTTP" },
                    },
                    Environment = environment
                        .Select(e => new CfnContainer.EnvironmentVariableProperty { Variable = e.Key, Value = e.Value })
                        .ToArray(),
                },
            },
            PublicEndpoint = new CfnContainer.PublicEndpointProperty
            {
                ContainerName = ContainerName,
                ContainerPort = ContainerPort,
                // Answers without CloudFront's origin header (EdgeOriginMiddleware), so Lightsail can call it directly.
                HealthCheckConfig = new CfnContainer.HealthCheckConfigProperty
                {
                    Path = "/health/live",
                    SuccessCodes = "200",
                    IntervalSeconds = 10,
                    TimeoutSeconds = 5,
                    HealthyThreshold = 2,
                    UnhealthyThreshold = 3,
                },
            },
        };
    }

    /// <summary>A key of the operator's JSON secret, as <c>{{resolve:secretsmanager:name:SecretString:key::}}</c>.</summary>
    private static string Operator(string key) =>
        SecretValue.SecretsManager(PermanentStack.OperatorSecretName, new SecretsManagerSecretOptions { JsonField = key }).UnsafeUnwrap();

    /// <summary>A whole generated secret, as <c>{{resolve:secretsmanager:name:SecretString:::}}</c>.</summary>
    private static string Whole(string name) => SecretValue.SecretsManager(name).UnsafeUnwrap();
}
