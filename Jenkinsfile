pipeline {
    agent any

    stages {
        stage('Checkout') {
            steps {
                checkout scm
            }
        }
        
        stage('Build & Deploy') {
            steps {
                script {
                    echo "Building and Deploying Apus AI Services..."
                    sh '''
                        set -euo pipefail
                        
                        # Ensure .env exists
                        if [ ! -f .env ]; then
                            echo "First-time setup: creating .env from .env.example"
                            cp .env.example .env
                            sed -i "s|POSTGRES_PASSWORD=change-me|POSTGRES_PASSWORD=$(openssl rand -hex 24)|" .env
                            sed -i "s|RABBITMQ_PASSWORD=change-me|RABBITMQ_PASSWORD=$(openssl rand -hex 24)|" .env
                            sed -i "s|JWT_SIGNING_KEY=.*|JWT_SIGNING_KEY=$(openssl rand -hex 32)|" .env
                        fi

                        # Deploy all services
                        docker compose up -d --build --remove-orphans
                        
                        echo "── Final service state ──"
                        docker compose ps
                    '''
                }
            }
        }

        stage('Verify Health') {
            steps {
                script {
                    echo "Checking health of critical services..."
                    sh '''
                        set +e
                        
                        # Check Postgres
                        for i in $(seq 1 12); do
                            if docker compose exec -T postgres pg_isready -U gateway > /dev/null 2>&1; then
                                echo "✅ Postgres is ready"
                                break
                            fi
                            if [ "$i" -eq 12 ]; then
                                echo "⚠️ Postgres failed to start within 60s"
                                docker compose logs --tail=20 postgres
                                exit 1
                            fi
                            sleep 5
                        done

                        # Check Backend API
                        for i in $(seq 1 18); do
                            if curl -fsS --max-time 3 http://localhost:9001/health/ready > /dev/null 2>&1; then
                                echo "✅ Backend API is ready"
                                break
                            fi
                            if [ "$i" -eq 18 ]; then
                                echo "⚠️ Backend API failed to respond within 90s"
                                docker compose logs --tail=30 gateway-api
                                exit 1
                            fi
                            sleep 5
                        done

                        # Check Frontend
                        for i in $(seq 1 18); do
                            if curl -fsS --max-time 3 -o /dev/null http://localhost:9002/; then
                                echo "✅ Frontend is ready"
                                break
                            fi
                            if [ "$i" -eq 18 ]; then
                                echo "⚠️ Frontend failed to respond within 90s"
                                docker compose logs --tail=30 frontend
                                exit 1
                            fi
                            sleep 5
                        done
                    '''
                }
            }
        }
    }

    post {
        always {
            sh 'docker image prune -f || true'
        }
        success {
            echo "Build & Deploy Pipeline completed successfully!"
        }
        failure {
            echo "Build & Deploy Pipeline failed! Please check the Verify Health stage logs."
        }
    }
}
