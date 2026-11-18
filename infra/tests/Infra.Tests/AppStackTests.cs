using System.Text.Json;
using Amazon.CDK.Assertions;

namespace Reservation.Infra.Tests;

/// <summary>The App stack (Design §17.4): a private database reachable from Lightsail only, and no secret in the template.</summary>
public class AppStackTests
{
    [Fact]
    public void The_database_is_private_encrypted_small_and_deletable()
    {
        var (_, app) = Synth.Templates(Synth.Full);

        var database = Synth.Single(app, "AWS::RDS::DBInstance");
        Assert.Equal("Delete", database.GetProperty("DeletionPolicy").GetString());
        var p = database.GetProperty("Properties");
        Assert.False(p.GetProperty("PubliclyAccessible").GetBoolean());
        Assert.True(p.GetProperty("StorageEncrypted").GetBoolean());
        Assert.False(p.GetProperty("DeletionProtection").GetBoolean());
        Assert.False(p.GetProperty("MultiAZ").GetBoolean());
        Assert.Equal("db.t4g.micro", p.GetProperty("DBInstanceClass").GetString());
        Assert.Equal("20", p.GetProperty("AllocatedStorage").GetString());
        Assert.Equal("gp3", p.GetProperty("StorageType").GetString());
        Assert.Equal("postgres", p.GetProperty("Engine").GetString());
        Assert.StartsWith("16", p.GetProperty("EngineVersion").GetString(), StringComparison.Ordinal);
        Assert.Equal(7, p.GetProperty("BackupRetentionPeriod").GetInt32());
    }

    [Fact]
    public void Postgres_is_open_to_the_lightsail_range_only()
    {
        var (_, app) = Synth.Templates(Synth.Full);

        var group = Synth.Single(app, "AWS::EC2::SecurityGroup").GetProperty("Properties");
        var ingress = Assert.Single(group.GetProperty("SecurityGroupIngress").EnumerateArray());
        Assert.Equal("172.26.0.0/16", ingress.GetProperty("CidrIp").GetString());
        Assert.Equal(5432, ingress.GetProperty("FromPort").GetInt32());
        Assert.Equal(5432, ingress.GetProperty("ToPort").GetInt32());
    }

    [Fact]
    public void There_is_no_nat_gateway_load_balancer_or_elastic_ip_in_either_stack()
    {
        var (permanent, app) = Synth.Templates(Synth.Full);

        foreach (var template in new[] { permanent, app })
        {
            template.ResourceCountIs("AWS::EC2::NatGateway", 0);
            template.ResourceCountIs("AWS::EC2::EIP", 0);
            template.ResourceCountIs("AWS::ElasticLoadBalancingV2::LoadBalancer", 0);
            template.ResourceCountIs("AWS::ElasticLoadBalancing::LoadBalancer", 0);
            template.ResourceCountIs("AWS::EC2::VPC", 0);
        }
    }

    [Fact]
    public void The_container_runs_the_demo_environment_with_every_secret_as_a_reference()
    {
        var (_, app) = Synth.Templates(Synth.Full);

        var service = Synth.Single(app, "AWS::Lightsail::Container").GetProperty("Properties");
        Assert.Equal("micro", service.GetProperty("Power").GetString());
        Assert.Equal(1, service.GetProperty("Scale").GetInt32());
        var deployment = service.GetProperty("ContainerServiceDeployment");
        var container = Assert.Single(deployment.GetProperty("Containers").EnumerateArray());
        Assert.Equal(":reservation-api.api.3", container.GetProperty("Image").GetString());
        var environment = container.GetProperty("Environment").EnumerateArray()
            .ToDictionary(e => e.GetProperty("Variable").GetString()!, e => e.GetProperty("Value"));

        Assert.Equal("Demo", environment["ASPNETCORE_ENVIRONMENT"].GetString());
        Assert.Equal("20261117-1200", environment["DEPLOY_ID"].GetString());
        var baseUrl = environment["GuestLinks__BaseUrl"].ToString();
        Assert.Contains("https://", baseUrl, StringComparison.Ordinal);
        var parameter = Synth.Json(app).GetProperty("Parameters").EnumerateObject().Single(p => baseUrl.Contains(p.Name, StringComparison.Ordinal));
        Assert.Equal("/reservation-demo/site-domain", parameter.Value.GetProperty("Default").GetString());
        foreach (var name in AppStack.SecretVariables)
        {
            Assert.True(environment.ContainsKey(name), $"{name} is missing");
            Assert.Contains("{{resolve:secretsmanager:", environment[name].ToString(), StringComparison.Ordinal);
        }

        Assert.Contains("SSL Mode=Require", environment["ConnectionStrings__Reservations"].ToString(), StringComparison.Ordinal);
        var health = deployment.GetProperty("PublicEndpoint").GetProperty("HealthCheckConfig");
        Assert.Equal("/health/live", health.GetProperty("Path").GetString());
    }

    [Fact]
    public void Nothing_in_either_template_looks_like_a_secret_value()
    {
        var (permanent, app) = Synth.Templates(Synth.Full);

        foreach (var template in new[] { permanent, app })
        {
            var json = Synth.Json(template).ToString();
            foreach (var marker in new[] { "sk_test_", "sk_live_", "whsec_", "pk_test_", "Password=" + "\"" })
            {
                Assert.DoesNotContain(marker, json, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Without_an_image_the_service_has_no_deployment()
    {
        var (_, app) = Synth.Templates(Synth.Full with { ImageName = null });

        var service = Synth.Single(app, "AWS::Lightsail::Container").GetProperty("Properties");
        Assert.False(service.TryGetProperty("ContainerServiceDeployment", out _));
    }

    [Fact]
    public void The_database_secret_goes_with_the_stack_and_has_no_fixed_name()
    {
        var (_, app) = Synth.Templates(Synth.Full);

        var secret = Synth.Single(app, "AWS::SecretsManager::Secret");
        Assert.Equal("Delete", secret.GetProperty("DeletionPolicy").GetString());
        Assert.False(secret.GetProperty("Properties").TryGetProperty("Name", out _));
    }

    [Fact]
    public void Both_stacks_are_tagged_with_the_project()
    {
        var (permanent, app) = Synth.Templates(Synth.Full);

        permanent.HasResourceProperties("AWS::S3::Bucket", new Dictionary<string, object>
        {
            ["Tags"] = Match.ArrayWith([new Dictionary<string, object> { ["Key"] = "Project", ["Value"] = "reservation-demo" }]),
        });
        app.HasResourceProperties("AWS::RDS::DBInstance", new Dictionary<string, object>
        {
            ["Tags"] = Match.ArrayWith([new Dictionary<string, object> { ["Key"] = "Project", ["Value"] = "reservation-demo" }]),
        });
    }

    [Fact]
    public void The_stacks_are_in_the_demo_account_in_sydney()
    {
        var app = new Amazon.CDK.App();
        var (permanent, appStack) = DemoApp.Build(app, Synth.Full);

        Assert.Equal("821797257125", permanent.Account);
        Assert.Equal("ap-southeast-2", appStack.Region);
        Assert.True(permanent.TerminationProtection);
        _ = JsonSerializer.Serialize(Template.FromStack(appStack).ToJSON());
    }
}
