# Mobile icon registry

Every SVG in `src/Presentation/CardiTrack.Mobile/Resources/Images`, what uses it, and every
colour it hard-codes. Written so a redesign can be scoped without grepping: the question
"what breaks if this icon changes" has an answer here.

**Generated, not hand-maintained.** Regenerate with:

```bash
python scripts/generate-icon-registry.py
```

A row that disagrees with the code means this file is stale, not that the code is wrong.

**89 icons**, **28 distinct colours**, **9 referenced nowhere**.

## Why the colours matter

Every icon hard-codes its own hex. None read `Colors.xaml`, because MAUI cannot tint an
`<Image>` source from a resource — so a palette change is a change to this many files, not
to one. That is the largest cost in any redesign of this set, and the reason this table
lists every colour rather than only the common ones: a colour used once still pins a file.

| Colour | Icons | Also known as |
| --- | --- | --- |
| `#1884DC` | 37 | Primary |
| `#FFFFFF` | 23 | White |
| `#939DAA` | 12 | MutedText |
| `#36C09B` | 9 | StatusGreen |
| `#174E86` | 7 | PrimaryDark |
| `#E53E3E` | 4 | ErrorRed |
| `#1F8A72` | 3 | MetricTemperatureInk / DatasetBodyText |
| `#C42F2F` | 3 | DangerRed |
| `#343434` | 2 | Body2Dark ink |
| `#999999` | 2 | — |
| `#F0A92E` | 2 | warning amber |
| `#123A5F` | 1 | — |
| `#1A6CB0` | 1 | — |
| `#1E8E5A` | 1 | — |
| `#2FA6C4` | 1 | — |
| `#34A853` | 1 | — |
| `#3E8AC7` | 1 | — |
| `#4285F4` | 1 | — |
| `#727272` | 1 | Body / Body2 ink |
| `#7C6FDC` | 1 | — |
| `#9FEBFF` | 1 | — |
| `#B45309` | 1 | — |
| `#B9F1FF` | 1 | — |
| `#CFEDE3` | 1 | — |
| `#E3F0FA` | 1 | — |
| `#EA4335` | 1 | — |
| `#ED7B2F` | 1 | — |
| `#FBBC05` | 1 | — |

## Unreferenced

In the tree, used by nothing. Candidates for deletion — but check the history first: an
icon can be staged ahead of a screen that has not shipped.

- `icon_back_dark.svg`
- `icon_clipboard_white.svg`
- `icon_monitoring.svg`
- `icon_tab_alerts_active.svg`
- `icon_tab_family.svg`
- `icon_tab_family_active.svg`
- `icon_tab_home_active.svg`
- `icon_tab_journal_active.svg`
- `icon_tab_settings_active.svg`

## The set

### Tab bar

| Icon | Colours | Used by |
| --- | --- | --- |
| `icon_tab_alerts.svg` | `#939DAA` | BottomNavBar, StatusHeroCard |
| `icon_tab_alerts_active.svg` | `#1884DC` `#174E86` `#36C09B` | **nothing** |
| `icon_tab_alerts_unread.svg` | `#F0A92E` | StatusHeroCard |
| `icon_tab_family.svg` | `#939DAA` | **nothing** |
| `icon_tab_family_active.svg` | `#174E86` `#1884DC` `#36C09B` | **nothing** |
| `icon_tab_home.svg` | `#939DAA` | BottomNavBar |
| `icon_tab_home_active.svg` | `#FFFFFF` `#174E86` `#1884DC` `#36C09B` | **nothing** |
| `icon_tab_journal.svg` | `#939DAA` | BottomNavBar, StatusHeroCard |
| `icon_tab_journal_active.svg` | `#174E86` `#1884DC` `#36C09B` | **nothing** |
| `icon_tab_journal_primary.svg` | `#1884DC` `#FFFFFF` | StatusHeroCard |
| `icon_tab_qa.svg` | `#939DAA` | StatusHeroCard |
| `icon_tab_qa_primary.svg` | `#1884DC` `#FFFFFF` | StatusHeroCard |
| `icon_tab_settings.svg` | `#939DAA` | BottomNavBar |
| `icon_tab_settings_active.svg` | `#174E86` `#1884DC` `#36C09B` | **nothing** |

### Status and severity

| Icon | Colours | Used by |
| --- | --- | --- |
| `icon_caution_danger.svg` | `#C42F2F` | FindingsList, MovementCards |
| `icon_status_check.svg` | `#36C09B` | ConnectionSuccessPage, DashboardPage, StatusHeroCard |
| `icon_status_critical.svg` | `#E53E3E` | StatusHeroCard |
| `icon_status_paused.svg` | `#939DAA` | StatusHeroCard |
| `icon_status_urgent.svg` | `#ED7B2F` | StatusHeroCard |
| `icon_status_warning.svg` | `#F0A92E` | AlertMiniCard, StatusHeroCard |
| `icon_trend_good.svg` | `#1F8A72` | MovementCards |
| `icon_trend_neutral.svg` | `#727272` | MovementCards |
| `icon_trend_watch.svg` | `#1884DC` | CardiMemberDetailPage |

### Metrics

| Icon | Colours | Used by |
| --- | --- | --- |
| `icon_metric_breathing.svg` | `#3E8AC7` | AlertDetailPage, MetricCard, TrendMetricCatalogue |
| `icon_metric_heart.svg` | `#E53E3E` | AlertDetailPage, AlertMiniCard, BaselineLearningPage, ConnectionSuccessPage, DeviceConnectionPage, MetricCard, TrendMetricCatalogue |
| `icon_metric_sleep.svg` | `#7C6FDC` | AlertDetailPage, AlertMiniCard, DeviceConnectionPage, MetricCard, TrendMetricCatalogue |
| `icon_metric_spo2.svg` | `#1F8A72` | MetricCard, TrendMetricCatalogue |
| `icon_metric_steps.svg` | `#1884DC` | AlertDetailPage, AlertMiniCard, ConnectionSuccessPage, DeviceConnectionPage, MetricCard, TrendMetricCatalogue |
| `icon_metric_temperature.svg` | `#1F8A72` | MetricCard, TrendMetricCatalogue |

### Actions

| Icon | Colours | Used by |
| --- | --- | --- |
| `icon_action_call.svg` | `#1884DC` | AlertListCard, CardiMemberDetailPage, QuickActionRow |
| `icon_action_check.svg` | `#1884DC` | AlertListCard, ChoiceSheetPage, FindingsList, InviteWaitPage |
| `icon_action_details.svg` | `#1884DC` | DashboardPage |
| `icon_action_profile.svg` | `#1884DC` | QuickActionRow |
| `icon_action_sms.svg` | `#1884DC` | CardiMemberDetailPage, QuickActionRow |

### Alert reasons

| Icon | Colours | Used by |
| --- | --- | --- |
| `icon_reason_activity_white.svg` | `#FFFFFF` | AlertDetailPage |
| `icon_reason_device_white.svg` | `#FFFFFF` | AlertDetailPage, ConnectionSuccessPage |
| `icon_reason_heart_white.svg` | `#FFFFFF` | AlertDetailPage, BaselineLearningPage |
| `icon_reason_monitoring_white.svg` | `#FFFFFF` | AlertDetailPage |
| `icon_reason_sleep_white.svg` | `#FFFFFF` | AlertDetailPage |

### Navigation and chrome

| Icon | Colours | Used by |
| --- | --- | --- |
| `icon_back_dark.svg` | `#1884DC` | **nothing** |
| `icon_back_white.svg` | `#FFFFFF` | AlertDetailPage, AlertSettingsPage, AlertsPage, CardiMemberDetailPage, DeviceManagementPage, EditCardiMemberPage, ExportConsentsPage, ExportHealthDataPage, JournalEntryPage, JournalPage, JournalTimingPage, LegalDocumentPage, MedicalInformationPage, MemberChatPage, MetricAlarmEditPage, MetricAlarmsPage, MetricTrendPage, NotificationPreferencesPage, NotificationsPage, QuestionnairesPage, SettingsPage, WizardHeader |
| `icon_caret_down.svg` | `#343434` | FilterChipBar |
| `icon_caret_down_white.svg` | `#FFFFFF` | FilterChipBar, MemberChatPage |
| `icon_chevron.svg` | `#939DAA` | AlertDetailPage, CardiMemberDetailPage, DeviceCard, DeviceManagementPage, MemberChatPage |
| `icon_chevron_down.svg` | `#939DAA` | AccordionSection, AlertListCard, ChoiceField, DeviceCard, DeviceManagementPage |
| `icon_chevron_down_white.svg` | `#FFFFFF` | MemberChatPage |

### Brand and third party

| Icon | Colours | Used by |
| --- | --- | --- |
| `apple_icon.svg` | `#FFFFFF` | CreateAccountPage, SignInPage |
| `google_icon.svg` | `#4285F4` `#34A853` `#FBBC05` `#EA4335` | CreateAccountPage, SignInPage |

### Everything else

| Icon | Colours | Used by |
| --- | --- | --- |
| `gradient_splash_bg.svg` | `#1A6CB0` `#1884DC` `#174E86` `#36C09B` `#2FA6C4` `#FFFFFF` | SplashPage |
| `icon_advise.svg` | `#939DAA` | CardiMemberDetailPage, StatusHeroCard |
| `icon_advise_primary.svg` | `#1884DC` | StatusHeroCard |
| `icon_bell.svg` | `#1884DC` | CardiMemberDetailPage, DashboardHeader |
| `icon_bell_cog.svg` | `#1884DC` | CardiMemberDetailPage |
| `icon_bell_plus.svg` | `#1884DC` | CardiMemberDetailPage |
| `icon_binoculars.svg` | `#CFEDE3` `#1884DC` `#36C09B` | CardiMemberDetailPage |
| `icon_book.svg` | `#1884DC` | CardiMemberDetailPage |
| `icon_book_cog.svg` | `#1884DC` | CardiMemberDetailPage |
| `icon_calendar.svg` | `#939DAA` | DateField, DeviceCard |
| `icon_callout.svg` | `#1884DC` | CardiMemberDetailPage |
| `icon_chatbot.svg` | `#36C09B` `#1884DC` `#174E86` `#9FEBFF` `#B9F1FF` `#123A5F` `#FFFFFF` | ChatBotLauncher, MemberChatPage, PendingBotIndicator |
| `icon_chatbot_mono.svg` | `#1884DC` | CardiMemberDetailPage |
| `icon_check_disc.svg` | `#E3F0FA` `#1884DC` | BaselineLearningPage, FindingsList |
| `icon_clipboard.svg` | `#1884DC` | CardiMemberDetailPage, InviteWaitPage |
| `icon_clipboard_white.svg` | `#FFFFFF` | **nothing** |
| `icon_edit.svg` | `#1884DC` | CardiMemberDetailPage, MedicalInformationPage |
| `icon_expand.svg` | `#1884DC` | MetricTrendCard |
| `icon_export_white.svg` | `#FFFFFF` | ExportAction |
| `icon_eye.svg` | `#999999` | CreateAccountPage, SignInPage |
| `icon_eye_off.svg` | `#999999` | CreateAccountPage, SignInPage |
| `icon_format_csv.svg` | `#1E8E5A` `#FFFFFF` | ExportFormatPopupPage |
| `icon_format_pdf.svg` | `#E53E3E` `#FFFFFF` | ExportFormatPopupPage |
| `icon_history_white.svg` | `#FFFFFF` | MemberChatPage |
| `icon_home_white.svg` | `#FFFFFF` | DashboardHeader, WizardHeader |
| `icon_medical_cross.svg` | `#1884DC` | CardiMemberDetailPage |
| `icon_monitoring.svg` | `#1884DC` | **nothing** |
| `icon_notification.svg` | `#FFFFFF` | FcmNotificationChannel, PushRegistrationCoordinator |
| `icon_open.svg` | `#1884DC` | ExportDeliveryPopupPage |
| `icon_pause.svg` | `#B45309` | CardiMemberDetailPage |
| `icon_person_remove.svg` | `#C42F2F` | CardiMemberDetailPage |
| `icon_plus.svg` | `#1884DC` | ConnectionSuccessPage, DeviceManagementPage |
| `icon_power_red.svg` | `#C42F2F` | SettingsPage |
| `icon_power_white.svg` | `#FFFFFF` | DashboardPage, SettingsPage |
| `icon_refresh.svg` | `#343434` | AlertsPage, ConnectionSuccessPage, DeviceCard |
| `icon_refresh_white.svg` | `#FFFFFF` | AlertsPage |
| `icon_save.svg` | `#1884DC` | ExportDeliveryPopupPage |
| `icon_share.svg` | `#1884DC` | ExportDeliveryPopupPage, InviteWaitPage |
| `icon_star.svg` | `#939DAA` | DeviceCard, StarRatingView |
| `icon_trash.svg` | `#E53E3E` | AlertDetailPage, AlertListCard, DeviceCard, MetricAlarmEditPage, QuestionCard |
| `icon_watch.svg` | `#1884DC` | CardiMemberDetailPage |

## How "used by" is decided

Every `.xaml` and `.cs` under `src/` is searched for the file name as a literal, which is
how icons are referenced throughout — `Source="icon_x.svg"`, or a string constant as in
`FindingsList.CheckMarker`. A name mentioned only in a comment therefore counts as a use, so
a row claiming a single caller is worth reading before trusting.
