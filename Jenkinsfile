/*
  Unified deploy pipeline for the YourCompany AI Gateway stack.

  Brings up all docker-compose services — postgres, redis, rabbitmq, clickhouse,
  gateway-api, analytics-worker, frontend, nginx — using docker-compose's layer
  cache, so unchanged services are skipped and only changed ones are rebuilt.

  To wire up the Jenkins job:
    New Item → Pipeline → name "Apus-AI"
    General  → Discard old builds → keep 30
    Build Triggers → GitHub hook trigger for GITScm polling
    Pipeline → Definition: Pipeline script from SCM
             → SCM: Git, URL: https://github.com/amrit90413/apus-ai.git
             → Branch: */main
             → Script Path: Jenkinsfile
    Save
*/

pipeline {
    agent any

    options {
        timestamps()
        timeout(time: 30, unit: 'MINUTES')
        disableConcurrentBuilds()
        buildDiscarder(logRotator(numToKeepStr: '30'))
    }

    triggers {
        githubPush()
    }

    stages {

        stage('Checkout') {
            steps {
                checkout scm
                sh 'git log -1 --oneline'
            }
        }

        stage('Ensure .env') {
            steps {
                sh '''
                    set -euo pipefail
                    if [ ! -f .env ]; then
                        echo "First-time setup: creating .env from .env.example"
                        cp .env.example .env
                        # Generate strong random values for required secrets
                        sed -i "s|POSTGRES_PASSWORD=change-me|POSTGRES_PASSWORD=$(openssl rand -hex 24)|" .env
                        sed -i "s|RABBITMQ_PASSWORD=change-me|RABBITMQ_PASSWORD=$(openssl rand -hex 24)|" .env
                        sed -i "s|JWT_SIGNING_KEY=.*|JWT_SIGNING_KEY=$(openssl rand -hex 32)|" .env
                        echo ".env created with generated secrets."
                    fi
                    echo ".env keys present:"
                    grep -o '^[A-Z_]*=' .env || true
                '''
            }
        }

        stage('Pull base images') {
            steps {
                sh 'docker compose pull --ignore-pull-failures || true'
            }
        }

        stage('Build & deploy') {
            steps {
                // --build   rebuilds services whose Dockerfile/context changed
                // -d        runs detached
                // --remove-orphans  cleans up renamed/removed services
                // No `compose down` first — avoids dropping Postgres data and
                // killing services that did not change.
                sh '''
                    set -euo pipefail
                    docker compose up -d --build --remove-orphans
                    echo "── Final service state ──"
                    docker compose ps
                '''
            }
        }

        stage('Health checks') {
            parallel {

                stage('Postgres') {
                    steps {
                        sh '''
                            set +e
                            for i in $(seq 1 12); do
                                if docker compose exec -T postgres pg_isready -U gateway > /dev/null 2>&1; then
                                    echo "Postgres ready"
                                    exit 0
                                fi
                                echo "  postgres attempt $i/12 (5s sleep)"
                                sleep 5
                            done
                            echo "⚠️  Postgres not ready after 60s — last logs:"
                            docker compose logs --tail=20 postgres
                            exit 1
                        '''
                    }
                }

                stage('Backend API') {
                    steps {
                        sh '''
                            set +e
                            for i in $(seq 1 18); do
                                if curl -fsS --max-time 3 http://localhost:9001/health/ready > /dev/null 2>&1; then
                                    echo "Backend API ready"
                                    exit 0
                                fi
                                echo "  backend attempt $i/18 (5s sleep)"
                                sleep 5
                            done
                            echo "⚠️  Backend did not respond within 90s — last 30 log lines:"
                            docker compose logs --tail=30 gateway-api
                            exit 1
                        '''
                    }
                }

                stage('Frontend') {
                    steps {
                        sh '''
                            set +e
                            for i in $(seq 1 18); do
                                if curl -fsS --max-time 3 -o /dev/null http://localhost:9002/; then
                                    echo "Frontend ready"
                                    exit 0
                                fi
                                echo "  frontend attempt $i/18 (5s sleep)"
                                sleep 5
                            done
                            echo "⚠️  Frontend did not respond within 90s — last 30 log lines:"
                            docker compose logs --tail=30 frontend
                            exit 1
                        '''
                    }
                }

            }
        }

    }

    post {
        success {
            sh '''
                echo ""
                echo "╔══════════════════════════════════════════════════════════════════╗"
                echo "║  ✅  YourCompany AI Gateway — deploy complete                    ║"
                echo "╠══════════════════════════════════════════════════════════════════╣"
                echo "║  Backend API  → http://34.131.116.1:9001                         ║"
                echo "║  Frontend     → http://34.131.116.1:9002                         ║"
                echo "║  Super-admin  → http://34.131.116.1:9002/super-admin             ║"
                echo "║  Postgres     → 34.131.116.1:5436  (db: gateway, user: gateway)  ║"
                echo "║  RabbitMQ UI  → http://34.131.116.1:15672                        ║"
                echo "╚══════════════════════════════════════════════════════════════════╝"
                echo ""
                echo "First deploy? Go to /super-admin and add your Anthropic API key."
            '''
        }
        failure {
            sh '''
                echo "❌ Build failed — recent logs from each service:"
                for svc in gateway-api frontend postgres redis rabbitmq analytics-worker; do
                    echo ""
                    echo "── ${svc} (last 20 lines) ──"
                    docker compose logs --tail=20 ${svc} 2>/dev/null || echo "(no logs)"
                done
            '''
        }
        always {
            sh 'docker compose ps || true'
        }
    }
}
