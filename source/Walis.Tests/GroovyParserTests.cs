using Walis.Core.Jenkins;

namespace Walis.Tests;

public class GroovyParserTests
{
    private const string Sample = """
                                  // Comment
                                  pipeline {
                                      agent { label 'linux' }
                                      environment {
                                          APP = 'walis'
                                          TOKEN = credentials('deploy-token')
                                      }
                                      options { timeout(time: 30, unit: 'MINUTES'); disableConcurrentBuilds() }
                                      parameters {
                                          string(name: 'ENV', defaultValue: 'stg', description: '目標環境')
                                          choice(name: 'REGION', choices: ['tw', 'jp'])
                                      }
                                      triggers { cron('H 2 * * *') }
                                      stages {
                                          stage('Build') {
                                              steps {
                                                  sh 'npm ci'
                                                  sh '''
                                                      npm run build
                                                      npm test
                                                  '''
                                                  echo "Building ${env.APP} for ${params.ENV}"
                                              }
                                          }
                                          stage('Deploy') {
                                              when { branch 'main' }
                                              steps {
                                                  dir('deploy') { sh "./deploy.sh ${params.ENV}" }
                                                  script {
                                                      def status = sh(script: 'git rev-parse HEAD', returnStdout: true).trim()
                                                      if (status) { echo 'has status' }
                                                  }
                                              }
                                          }
                                      }
                                      post {
                                          always { archiveArtifacts artifacts: 'dist/**', fingerprint: true }
                                          failure { mail to: 'team@example.com', subject: 'Failed' }
                                      }
                                  }
                                  """;

    private static GroovyCall Call(IReadOnlyList<GroovyNode> nodes, string name)
    {
        return Assert.IsType<GroovyCall>(nodes.Single(node => node is GroovyCall call && call.Name == name));
    }

    [Fact]
    public void DeclarativePipelineStructureIsExtracted()
    {
        var nodes = GroovyParser.Parse(Sample);
        var pipeline = Call(nodes, "pipeline");
        Assert.NotNull(pipeline.Body);

        var agent = Call(pipeline.Body!, "agent");
        var label = Call(agent.Body!, "label");
        Assert.Equal("linux", Assert.IsType<GroovyString>(label.First).Text);

        var environment = Call(pipeline.Body!, "environment");
        var assignments = environment.Body!.OfType<GroovyAssignment>().ToList();
        Assert.Equal("APP", assignments[0].Target);
        Assert.Equal("walis", Assert.IsType<GroovyString>(assignments[0].Value).Text);
        var credentials = Assert.IsType<GroovyCallValue>(assignments[1].Value).Call;
        Assert.Equal("credentials", credentials.Name);

        var options = Call(pipeline.Body!, "options");
        var timeout = Call(options.Body!, "timeout");
        Assert.Equal("30", Assert.IsType<GroovyNumber>(timeout.Named("time")).Text);
        Assert.Equal("MINUTES", Assert.IsType<GroovyString>(timeout.Named("unit")).Text);
        Assert.Contains(options.Body!, node => node is GroovyCall { Name: "disableConcurrentBuilds" });

        var parameters = Call(pipeline.Body!, "parameters");
        var choice = Call(parameters.Body!, "choice");
        var choices = Assert.IsType<GroovyList>(choice.Named("choices"));
        Assert.Equal(["tw", "jp"], choices.Items.Cast<GroovyString>().Select(item => item.Text));

        var stages = Call(pipeline.Body!, "stages");
        var stageCalls = stages.Body!.OfType<GroovyCall>().ToList();
        Assert.Equal(["Build", "Deploy"], stageCalls.Select(stage => ((GroovyString)stage.First!).Text));

        var buildSteps = Call(stageCalls[0].Body!, "steps");
        var shells = buildSteps.Body!.OfType<GroovyCall>().Where(call => call.Name == "sh").ToList();
        Assert.Equal("npm ci", ((GroovyString)shells[0].First!).Text);
        Assert.Contains("npm run build", ((GroovyString)shells[1].First!).Text);
        var echo = Call(buildSteps.Body!, "echo");
        var message = Assert.IsType<GroovyString>(echo.First);
        Assert.True(message.Interpolated);
        Assert.Contains("${env.APP}", message.Text);

        var deploy = stageCalls[1];
        var when = Call(deploy.Body!, "when");
        Assert.Equal("main", ((GroovyString)Call(when.Body!, "branch").First!).Text);
        var deploySteps = Call(deploy.Body!, "steps");
        var dir = Call(deploySteps.Body!, "dir");
        Assert.Equal("deploy", ((GroovyString)dir.First!).Text);
        Assert.Single(dir.Body!);

        var script = Call(deploySteps.Body!, "script");
        var declaration = Assert.IsType<GroovyAssignment>(script.Body![0]);
        Assert.True(declaration.IsDeclaration);
        Assert.IsType<GroovyRawValue>(declaration.Value);
        Assert.IsType<GroovyRaw>(script.Body[1]);

        var post = Call(pipeline.Body!, "post");
        var always = Call(post.Body!, "always");
        var archive = Call(always.Body!, "archiveArtifacts");
        Assert.Equal("dist/**", ((GroovyString)archive.Named("artifacts")!).Text);
        var failure = Call(post.Body!, "failure");
        var mail = Call(failure.Body!, "mail");
        Assert.Equal("team@example.com", ((GroovyString)mail.Named("to")!).Text);
    }

    [Fact]
    public void ScriptedPipelineAndUnknownStatementsDoNotFailParsing()
    {
        var nodes = GroovyParser.Parse("""
                                       node('builder') {
                                           stage('Checkout') { checkout scm }
                                           stage('Test') {
                                               try { sh 'make test' } catch (e) { currentBuild.result = 'FAILURE' }
                                               parallel unit: { sh 'make unit' }, lint: { sh 'make lint' }
                                           }
                                           for (int i = 0; i < 3; i++) { echo "loop ${i}" }
                                       }
                                       """);

        var node = Call(nodes, "node");
        var stages = node.Body!.OfType<GroovyCall>().Where(call => call.Name == "stage").ToList();
        Assert.Equal(2, stages.Count);
        var checkout = Call(stages[0].Body!, "checkout");
        Assert.Equal("scm", Assert.IsType<GroovyIdentifier>(checkout.First).Name);

        var test = stages[1].Body!;
        Assert.IsType<GroovyRaw>(test[0]);
        var parallel = Call(test, "parallel");
        Assert.Equal(["unit", "lint"], parallel.Arguments.Select(argument => argument.Name));
        Assert.IsType<GroovyClosure>(parallel.Arguments[0].Value);

        Assert.Contains(node.Body!, item => item is GroovyRaw raw && raw.Text.StartsWith("for ("));
    }
}