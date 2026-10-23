// 前端專案：容器內建置、平行測試、只在 main 部署、post 收產出
pipeline {
    agent { docker { image 'node:20'; args '-e CI=true' } }

    environment {
        APP_NAME = 'storefront'
        NPM_TOKEN = credentials('npm-token')
    }

    options {
        timeout(time: 30, unit: 'MINUTES')
        disableConcurrentBuilds()
        buildDiscarder(logRotator(numToKeepStr: '20'))
    }

    parameters {
        choice(name: 'DEPLOY_ENV', choices: ['stg', 'prod'], description: '部署環境')
        booleanParam(name: 'SKIP_TESTS', defaultValue: false, description: '跳過測試')
    }

    triggers { cron('H 3 * * 1-5') }

    stages {
        stage('Install') {
            steps {
                sh 'npm ci'
            }
        }
        stage('Build') {
            steps {
                sh """
                    npm run build -- --mode ${params.DEPLOY_ENV}
                    echo "built ${env.APP_NAME} #${BUILD_NUMBER}"
                """
            }
        }
        stage('Test') {
            when { environment name: 'SKIP_TESTS', value: 'false' }
            parallel {
                stage('Unit') { steps { sh 'npm run test:unit -- --reporter junit --outputFile reports/unit.xml' } }
                stage('Lint') { steps { sh 'npm run lint' } }
                stage('E2E') {
                    options { retry(2) }
                    steps { sh 'npm run test:e2e' }
                }
            }
        }
        stage('Deploy') {
            when { branch 'main' }
            steps {
                withCredentials([usernamePassword(credentialsId: 'cdn', usernameVariable: 'CDN_USER', passwordVariable: 'CDN_PASS')]) {
                    dir('deploy') {
                        sh './upload.sh dist "$DEPLOY_ENV"'
                    }
                }
            }
            post {
                failure { sh './deploy/rollback.sh' }
            }
        }
    }

    post {
        always {
            archiveArtifacts artifacts: 'dist/**', fingerprint: true
            junit 'reports/*.xml'
        }
        failure { slackSend channel: '#frontend', message: "${env.JOB_NAME} #${env.BUILD_NUMBER} failed" }
    }
}
