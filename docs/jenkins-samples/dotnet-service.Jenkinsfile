// .NET 服務：指定節點、工具、巢狀 stage、矩陣設定、下游觸發
pipeline {
    agent { label 'linux && dotnet' }

    tools { dotnetsdk 'dotnet-10' }

    options {
        timeout(time: 1, unit: 'HOURS')
        retry(1)
        timestamps()
    }

    parameters {
        string(name: 'VERSION_SUFFIX', defaultValue: 'ci', description: '版本後綴')
        text(name: 'RELEASE_NOTES', defaultValue: '', description: '發行說明')
    }

    triggers { pollSCM('H/5 * * * *') }

    stages {
        stage('Restore') {
            steps { sh 'dotnet restore Service.sln' }
        }
        stage('Build & Test') {
            matrix {
                axes {
                    axis { name 'CONFIGURATION'; values 'Debug', 'Release' }
                }
                stages {
                    stage('Build') {
                        steps { sh 'dotnet build Service.sln -c $CONFIGURATION --no-restore' }
                    }
                    stage('Test') {
                        steps { sh 'dotnet test Service.sln -c $CONFIGURATION --no-build --logger "trx;LogFileName=results.trx"' }
                    }
                }
            }
        }
        stage('Package') {
            stages {
                stage('Publish') {
                    steps { sh "dotnet publish src/Service -c Release -o out --version-suffix ${params.VERSION_SUFFIX}" }
                }
                stage('Archive') {
                    steps {
                        sh 'tar czf service.tgz -C out .'
                        archiveArtifacts artifacts: 'service.tgz'
                    }
                }
            }
        }
        stage('Trigger deploy') {
            steps {
                build job: 'service-deploy-stg', wait: false, parameters: [string(name: 'ARTIFACT', value: 'service.tgz')]
            }
        }
    }

    post {
        cleanup { cleanWs() }
        unstable { echo 'tests were unstable' }
    }
}
