# CardiTrack — Google for Startups Cloud Credits Application

## Company Overview

**CardiTrack** is an AI-powered health monitoring platform that helps family caregivers keep elderly loved ones safe at home — affordably. We connect to wearable devices people already own (Fitbit, Apple Watch, Garmin, Samsung) and use machine learning to detect health decline *before* it becomes an emergency.

We are currently in active development, targeting a public beta in Q3 2026.

---

## The Problem

53 million Americans are caring for an elderly family member. They worry constantly — and for good reason. Falls, cardiac events, and gradual cognitive decline often go undetected until a 911 call is made.

Traditional medical alert systems charge $47–68/month for reactive, one-size-fits-all hardware. Most elderly people refuse to wear them. Meanwhile, 27 million+ seniors in the US already own a Fitbit, Apple Watch, or Garmin — and no one is using that data intelligently.

---

## Our Solution

CardiTrack monitors the wearable data elderly users generate every day and applies personalized AI baselines to detect anomalies — unusual inactivity, elevated resting heart rate, disrupted sleep patterns, gradual mobility decline — and surfaces alerts to the family caregiver's web or mobile dashboard.

**Key capabilities:**
- Multi-device support — works with 7+ consumer wearable brands, no proprietary hardware
- Personalized AI anomaly detection — MedGemma 1.5 4B (Google's medical LLM) with SSA-LSTM per-user baselines — targeting <5% false positive rate vs. 20–30% industry standard
- Five alert types: activity decline, heart rate elevation, sleep disruption, no morning activity, long-term trend
- Family collaboration — multiple caregivers monitoring one or more elderly members
- HIPAA-compliant audit logs, encrypted health notes, and FHIR R4 / HL7 v2 data exports
- Web dashboard (Blazor Server) and mobile app (.NET MAUI for iOS and Android)

---

## Target Market

| Segment | Size |
|---|---|
| US family caregivers | 53 million |
| US adults 65+ | 59 million |
| EU adults 65+ | 90 million |
| Seniors who already own compatible wearables | 27 million+ (US) |

**TAM**: $9B+ globally. Market growing at 19.5% CAGR (2024–2030).

---

## Business Model

| Plan | Price | Target |
|---|---|---|
| Basic Care | $8/month | Budget-conscious families |
| Complete Care | $15/month | Core offering — 3 CardiMembers, real-time alerts |
| Guardian Plus | $29.99/month | Power users, multi-member households |
| Enterprise | $5–10/resident/month | Assisted living facilities |

**Unit economics (Tier 2):** ~$13/month gross profit per subscriber, $156/year LTV baseline. CAC target: <$50.

---

## Why We Need Cloud Credits

CardiTrack is built on a cloud-native, infrastructure-as-code stack (Terraform, Docker, GitHub Actions CI/CD). Our current and near-term cloud workloads include:

- **Background worker service** — continuous polling and processing of wearable API data for all monitored users
- **ML inference pipeline** — running personalized anomaly detection models per CardiMember
- **Real-time alerting** — SignalR-powered push notifications to web and mobile clients
- **HIPAA-compliant data storage** — encrypted PostgreSQL, audit logging, backup and retention
- **Multi-device API integration** — Fitbit, Apple HealthKit, Garmin Connect, Samsung Health
- **CI/CD and staging environments** — GitHub Actions pipelines for continuous deployment

Google Cloud credits would allow us to:
1. Complete MVP 1 development and run a private beta with 20–50 families
2. Scale to public launch (Q2 2026) with confidence in infrastructure costs
3. Validate unit economics before committing to paid cloud spend at scale

---

## Traction & Roadmap

| Milestone | Timeline |
|---|---|
| MVP 1 — Fitbit support, web dashboard, alerts | Q1 2026 |
| Private beta — 20–50 families | Q1–Q2 2026 |
| Public launch | Q2 2026 |
| Apple Watch & Garmin support | Q3 2026 |
| 1,000+ paying subscribers | Q4 2026 |
| Enterprise / assisted living tier | 2027 |
| UK, Canada, Australia expansion | 2027+ |

---

## Team

CardiTrack is being built by a founder with full-stack .NET development experience, designing the platform end-to-end — backend API, ML pipeline, Blazor web dashboard, MAUI mobile app, and cloud infrastructure.

---

## Summary

CardiTrack addresses a large, underserved market with a meaningfully differentiated product: 50–70% cheaper than incumbents, preventive rather than reactive, compatible with devices people already own, and powered by personalized AI. Google Cloud credits would directly accelerate our ability to get a working product into the hands of families who need it.
