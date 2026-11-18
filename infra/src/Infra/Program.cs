using Amazon.CDK;
using Reservation.Infra;

// The AWS demo (ADR-005, Design §17.4): two stacks in the demo account, Sydney. Read-only commands (synth, diff, ls) are
// safe to run; deploy, destroy and bootstrap are the operator's, from docs/RUNBOOK-aws.md.
var app = new App();
var config = DemoConfig.From(app.Node);
DemoApp.Build(app, config);
app.Synth();
