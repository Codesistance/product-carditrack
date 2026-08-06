# Global Load Balancer + Cloud CDN + Cloud Armor (WAF)
# Architecture: Internet → Cloud Armor (WAF) → GCLB + Cloud CDN → Cloud Run

# Locals
locals {
  has_any_domain     = var.api_custom_domain != "" || var.web_custom_domain != ""
  lb_name_prefix     = trimsuffix(var.api_service_name, "-api")
  configured_domains = compact([var.web_custom_domain, var.api_custom_domain])
  domain_expression  = "!(${join(" || ", [for d in local.configured_domains : "request.headers['host'].lower() == '${d}'"])})"
}

# ── Global static IP ───────────────────────────────────────────────────────────
resource "google_compute_global_address" "lb" {
  count = local.has_any_domain ? 1 : 0
  name  = "${local.lb_name_prefix}-lb-ip"

  depends_on = [google_project_service.compute]
}

# ── Managed SSL certificates (Google-managed, auto-renewed) ───────────────────
resource "google_compute_managed_ssl_certificate" "api" {
  count = var.api_custom_domain != "" ? 1 : 0
  name  = "${var.api_service_name}-cert"
  managed {
    domains = [var.api_custom_domain]
  }
  depends_on = [google_project_service.compute]
}

resource "google_compute_managed_ssl_certificate" "web" {
  count = var.web_custom_domain != "" ? 1 : 0
  name  = "${var.web_service_name}-cert"
  managed {
    domains = [var.web_custom_domain]
  }
  depends_on = [google_project_service.compute]
}

# ── SSL policy — TLS 1.2+ MODERN (HTTPS optimized) ───────────────────────────
resource "google_compute_ssl_policy" "main" {
  count           = local.has_any_domain ? 1 : 0
  name            = "${local.lb_name_prefix}-ssl-policy"
  profile         = "MODERN"
  min_tls_version = "TLS_1_2"
  depends_on      = [google_project_service.compute]
}

# ── Cloud Armor WAF security policy ───────────────────────────────────────────
resource "google_compute_security_policy" "waf" {
  count = local.has_any_domain ? 1 : 0
  name  = "${local.lb_name_prefix}-waf"

  # Block requests not using a configured domain name (prevents direct IP access)
  rule {
    action   = "deny(403)"
    priority = 40
    match {
      expr { expression = local.domain_expression }
    }
    description = "Block requests that do not use a configured domain name"
  }

  # Block known bad user agents
  rule {
    action   = "deny(403)"
    priority = 50
    match {
      expr { expression = "request.headers['user-agent'].matches('(?i)curl.*') || request.headers['user-agent'].matches('(?i)libredtail-http.*') || request.headers['user-agent'].matches('(?i)go-http-client/1[.]1.*') || request.headers['user-agent'].matches('(?i).*censysinspect.*')" }
    }
    description = "Block known bad user agents (curl, libredtail-http, Go-http-client/1.1, CensysInspect)"
  }

  # Block requests to sensitive file extensions
  rule {
    action   = "deny(403)"
    priority = 60
    match {
      expr { expression = "request.path.matches('(?i).*[.](?:config|xml|php|env|yaml|toml|cfg|conf|gpg)$')" }
    }
    description = "Block requests to sensitive file extensions"
  }

  # Block CMS/WordPress scanner paths (probes for software we do not run;
  # *.php probes such as xmlrpc.php are already denied by the extension rule above)
  rule {
    action   = "deny(403)"
    priority = 70
    match {
      expr { expression = "request.path.matches('(?i)/(?:wp-json|wp-admin|wp-content|wp-includes)(?:/.*)?')" }
    }
    description = "Block CMS/WordPress scanner paths (wp-json, wp-admin, wp-content, wp-includes)"
  }

  # Rate limiting — 100 req/min per IP
  rule {
    action   = "throttle"
    priority = 100
    match {
      versioned_expr = "SRC_IPS_V1"
      config { src_ip_ranges = ["*"] }
    }
    rate_limit_options {
      conform_action = "allow"
      exceed_action  = "deny(429)"
      rate_limit_threshold {
        count        = 100
        interval_sec = 60
      }
    }
    description = "Rate limiting - 100 req/min per IP"
  }

  # OWASP XSS
  rule {
    action   = "deny(403)"
    priority = 1000
    match {
      expr { expression = "evaluatePreconfiguredWaf('xss-v33-stable')" }
    }
    description = "OWASP XSS protection"
  }

  # OWASP SQLi
  rule {
    action   = "deny(403)"
    priority = 1001
    match {
      expr { expression = "evaluatePreconfiguredWaf('sqli-v33-stable')" }
    }
    description = "OWASP SQLi protection"
  }

  # OWASP RCE
  rule {
    action   = "deny(403)"
    priority = 1002
    match {
      expr { expression = "evaluatePreconfiguredWaf('rce-v33-stable')" }
    }
    description = "OWASP RCE protection"
  }

  # OWASP LFI (path traversal)
  rule {
    action   = "deny(403)"
    priority = 1003
    match {
      expr { expression = "evaluatePreconfiguredWaf('lfi-v33-stable')" }
    }
    description = "OWASP LFI protection"
  }

  # Default allow
  rule {
    action   = "allow"
    priority = 2147483647
    match {
      versioned_expr = "SRC_IPS_V1"
      config { src_ip_ranges = ["*"] }
    }
    description = "Default allow"
  }

  depends_on = [google_project_service.compute]
}

# ── Serverless NEGs (connect LB to Cloud Run) ─────────────────────────────────
resource "google_compute_region_network_endpoint_group" "api" {
  count                 = local.has_any_domain ? 1 : 0
  name                  = "${var.api_service_name}-neg"
  network_endpoint_type = "SERVERLESS"
  region                = var.cloud_run_location
  cloud_run {
    service = google_cloud_run_v2_service.api.name
  }
  depends_on = [google_project_service.compute]
}

resource "google_compute_region_network_endpoint_group" "web" {
  count                 = local.has_any_domain ? 1 : 0
  name                  = "${var.web_service_name}-neg"
  network_endpoint_type = "SERVERLESS"
  region                = var.cloud_run_location
  cloud_run {
    service = google_cloud_run_v2_service.web.name
  }
  depends_on = [google_project_service.compute]
}

# ── Backend services ───────────────────────────────────────────────────────────
# API — no CDN (dynamic responses), WAF enabled
resource "google_compute_backend_service" "api" {
  count                 = local.has_any_domain ? 1 : 0
  name                  = "${var.api_service_name}-backend"
  load_balancing_scheme = "EXTERNAL_MANAGED"
  protocol              = "HTTPS"
  security_policy       = google_compute_security_policy.waf[0].id
  enable_cdn            = false

  log_config {
    enable      = true
    sample_rate = 1.0
  }

  backend {
    group = google_compute_region_network_endpoint_group.api[0].id
  }

  depends_on = [google_project_service.compute]
}

# Web — Cloud CDN enabled for static assets, WAF enabled
resource "google_compute_backend_service" "web" {
  count                 = local.has_any_domain ? 1 : 0
  name                  = "${var.web_service_name}-backend"
  load_balancing_scheme = "EXTERNAL_MANAGED"
  protocol              = "HTTPS"
  security_policy       = google_compute_security_policy.waf[0].id
  enable_cdn            = true

  log_config {
    enable      = true
    sample_rate = 1.0
  }

  cdn_policy {
    cache_mode        = "USE_ORIGIN_HEADERS"
    default_ttl       = 300
    client_ttl        = 300
    max_ttl           = 86400
    negative_caching  = true
    serve_while_stale = 86400

    cache_key_policy {
      include_host         = true
      include_protocol     = true
      include_query_string = true
    }
  }

  backend {
    group = google_compute_region_network_endpoint_group.web[0].id
  }

  depends_on = [google_project_service.compute]
}

# ── URL map — route by hostname ────────────────────────────────────────────────
resource "google_compute_url_map" "main" {
  count           = local.has_any_domain ? 1 : 0
  name            = "${local.lb_name_prefix}-lb"
  default_service = google_compute_backend_service.web[0].id

  dynamic "host_rule" {
    for_each = var.api_custom_domain != "" ? [var.api_custom_domain] : []
    content {
      hosts        = [host_rule.value]
      path_matcher = "api"
    }
  }

  dynamic "path_matcher" {
    for_each = var.api_custom_domain != "" ? [var.api_custom_domain] : []
    content {
      name            = "api"
      default_service = google_compute_backend_service.api[0].id
    }
  }
}

# ── HTTP → HTTPS redirect ──────────────────────────────────────────────────────
resource "google_compute_url_map" "https_redirect" {
  count = local.has_any_domain ? 1 : 0
  name  = "${local.lb_name_prefix}-https-redirect"

  default_url_redirect {
    https_redirect         = true
    redirect_response_code = "MOVED_PERMANENTLY_DEFAULT"
    strip_query            = false
  }
}

resource "google_compute_target_http_proxy" "redirect" {
  count   = local.has_any_domain ? 1 : 0
  name    = "${local.lb_name_prefix}-http-proxy"
  url_map = google_compute_url_map.https_redirect[0].id
}

resource "google_compute_global_forwarding_rule" "http_redirect" {
  count                 = local.has_any_domain ? 1 : 0
  name                  = "${local.lb_name_prefix}-http-redirect"
  target                = google_compute_target_http_proxy.redirect[0].id
  port_range            = "80"
  ip_address            = google_compute_global_address.lb[0].address
  load_balancing_scheme = "EXTERNAL_MANAGED"
}

# ── HTTPS proxy + forwarding rule ─────────────────────────────────────────────
resource "google_compute_target_https_proxy" "main" {
  count   = local.has_any_domain ? 1 : 0
  name    = "${local.lb_name_prefix}-https-proxy"
  url_map = google_compute_url_map.main[0].id
  ssl_certificates = concat(
    var.web_custom_domain != "" ? [google_compute_managed_ssl_certificate.web[0].id] : [],
    var.api_custom_domain != "" ? [google_compute_managed_ssl_certificate.api[0].id] : [],
  )
  ssl_policy = google_compute_ssl_policy.main[0].id
}

resource "google_compute_global_forwarding_rule" "https" {
  count                 = local.has_any_domain ? 1 : 0
  name                  = "${local.lb_name_prefix}-https"
  target                = google_compute_target_https_proxy.main[0].id
  port_range            = "443"
  ip_address            = google_compute_global_address.lb[0].address
  load_balancing_scheme = "EXTERNAL_MANAGED"
}

# ── Outputs ────────────────────────────────────────────────────────────────────
output "lb_ip_address" {
  description = "Add this as an A record in your DNS for both custom domains"
  value       = local.has_any_domain ? google_compute_global_address.lb[0].address : null
}
