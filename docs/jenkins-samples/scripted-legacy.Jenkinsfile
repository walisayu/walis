// 舊式腳本式管線：properties、node、try/catch、平行分支、withEnv、stash
properties([
    pipelineTriggers([cron('@daily')]),
    parameters([
        string(name: 'BRANCH', defaultValue: 'main'),
        booleanParam(name: 'CLEAN', defaultValue: true)
    ]),
    disableConcurrentBuilds()
])

node('builder') {
    stage('Checkout') {
        checkout scm
        if (params.CLEAN) {
            deleteDir()
            checkout scm
        }
    }

    stage('Build') {
        withEnv(['GOFLAGS=-mod=vendor', 'CGO_ENABLED=0']) {
            sh 'go build ./...'
        }
        stash name: 'binaries', includes: 'bin/**'
    }

    stage('Test') {
        try {
            sh 'go test ./... -coverprofile=coverage.out'
        } catch (err) {
            currentBuild.result = 'UNSTABLE'
            echo "tests failed: ${err}"
        }
    }

    parallel(
        lint: { sh 'golangci-lint run' },
        vet: { sh 'go vet ./...' },
        failFast: true
    )

    stage('Package') {
        unstash 'binaries'
        timeout(time: 10, unit: 'MINUTES') {
            sh "tar czf app-${BUILD_NUMBER}.tgz bin"
        }
        archiveArtifacts artifacts: 'app-*.tgz'
    }
}
