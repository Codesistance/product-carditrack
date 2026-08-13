terraform {
  required_version = ">= 1.14.7"

  required_providers {
    google = {
      source  = "hashicorp/google"
      version = "~> 7.23"
    }
    google-beta = {
      source  = "hashicorp/google-beta"
      version = "~> 7.23"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
    }
    # Used for one thing only: waiting out Secret Manager IAM propagation before Cloud Run
    # validates a revision's secret references against a newly created service account. See
    # the time_sleep resources in deployments/service_accounts.tf.
    time = {
      source  = "hashicorp/time"
      version = "~> 0.11"
    }
  }
}
