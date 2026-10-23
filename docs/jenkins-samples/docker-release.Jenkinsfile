// 映像發行：密碼參數、checkout 指定儲存庫、人工核准（不支援）、catchError、error
pipeline {
    agent any

    parameters {
        string(name: 'TAG', defaultValue: 'latest', description: '映像標籤')
        password(name: 'REGISTRY_PASSWORD', description: '登錄伺服器密碼')
        choice(name: 'TARGET', choices: 'stg\nprod', description: '目標')
    }

    environment {
        REGISTRY = 'registry.example.com'
        IMAGE = "${REGISTRY}/app"
    }

    stages {
        stage('Checkout') {
            steps {
                git url: 'https://git.example.com/team/app.git', branch: 'release', credentialsId: 'git-deploy'
            }
        }
        stage('Build image') {
            steps {
                sh '''
                    docker build -t "$IMAGE:$TAG" .
                    docker tag "$IMAGE:$TAG" "$IMAGE:build-$BUILD_NUMBER"
                '''
            }
        }
        stage('Smoke') {
            steps {
                catchError(buildResult: 'SUCCESS', stageResult: 'FAILURE') {
                    sh 'docker run --rm "$IMAGE:$TAG" ./smoke.sh'
                }
            }
        }
        stage('Approve') {
            when { equals expected: 'prod', actual: params.TARGET }
            input { message 'Push to production?'; ok 'Push' }
            steps { echo 'approved' }
        }
        stage('Push') {
            steps {
                sh 'echo "$REGISTRY_PASSWORD" | docker login "$REGISTRY" -u ci --password-stdin'
                sh 'docker push "$IMAGE:$TAG"'
                script {
                    env.PUSHED_AT = sh(script: 'date -u +%FT%TZ', returnStdout: true).trim()
                    if (params.TARGET == 'prod') {
                        echo 'production push'
                    }
                }
            }
        }
        stage('Verify') {
            steps {
                sh 'docker pull "$IMAGE:$TAG" || error "image missing"'
                error 'stop here on purpose'
            }
        }
    }

    post {
        always { sh 'docker logout "$REGISTRY" || true' }
        failure { mail to: 'ops@example.com', subject: "release ${env.BUILD_NUMBER} failed", body: 'see console' }
    }
}
