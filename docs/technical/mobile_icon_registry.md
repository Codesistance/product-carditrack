# Mobile icon registry

Every SVG in `src/Presentation/CardiTrack.Mobile/Resources/Images`, what uses it, and every
colour it hard-codes. Written so a redesign can be scoped without grepping: the question
"what breaks if this icon changes" has an answer here.

**Generated, not hand-maintained.** Regenerate with:

```bash
python scripts/generate-icon-registry.py
```

A row that disagrees with the code means this file is stale, not that the code is wrong.

**96 icons**, **34 distinct colours**, **3 referenced nowhere**.

## Why the colours matter

Every icon hard-codes its own hex. None read `Colors.xaml`, because MAUI cannot tint an
`<Image>` source from a resource — so a palette change is a change to this many files, not
to one. That is the largest cost in any redesign of this set, and the reason this table
lists every colour rather than only the common ones: a colour used once still pins a file.

| Colour | Icons | Also known as |
| --- | --- | --- |
| `#FFFFFF` | 28 | White |
| `#153D66` | 24 | icons.json brand outer stroke |
| `#3175B9` | 19 | icons.json brand and activity fill, steel blue |
| `#939DAA` | 14 | MutedText |
| `#1884DC` | 11 | Primary |
| `#C42F2F` | 10 | DangerRed |
| `#174E86` | 7 | PrimaryDark |
| `#36C09B` | 7 | StatusGreen |
| `#727272` | 7 | Body / Body2 ink |
| `#FBE4E4` | 6 | derived: StatusRed at 14% over white |
| `#1F8A72` | 5 | MetricTemperatureInk / DatasetBodyText |
| `#D9DCE1` | 5 | — |
| `#B45309` | 3 | severity orange ink |
| `#861B1B` | 2 | — |
| `#A9741A` | 2 | DatasetWarningText |
| `#123A5F` | 1 | — |
| `#1A6CB0` | 1 | — |
| `#2FA6C4` | 1 | — |
| `#343434` | 1 | Body2Dark ink |
| `#34A853` | 1 | — |
| `#3E8AC7` | 1 | MetricBreathingInk |
| `#4285F4` | 1 | — |
| `#7C6FDC` | 1 | MetricSleepInk |
| `#9FEBFF` | 1 | — |
| `#B9F1FF` | 1 | — |
| `#C9E1FF` | 1 | MetricTileTint, opaque (brand internal fill) |
| `#E4F6F2` | 1 | DatasetBodyBackground |
| `#E53E3E` | 1 | ErrorRed |
| `#EA4335` | 1 | — |
| `#EEF1F5` | 1 | DatasetOtherBackground |
| `#F0A92E` | 1 | warning amber |
| `#FBBC05` | 1 | — |
| `#FCEDE2` | 1 | derived: StatusOrange at 14% over white |
| `#FFF3DE` | 1 | DatasetWarningBackground |

## Unreferenced

In the tree, used by nothing. Candidates for deletion — but check the history first: an
icon can be staged ahead of a screen that has not shipped.

- `icon_caution_danger.svg`
- `icon_status_critical.svg`
- `icon_status_urgent.svg`

## The set

### Tab bar

| Icon | Colours | Used by |
| --- | --- | --- |
| `icon_tab_alerts.svg` | `#939DAA` | BottomNavBar, StatusHeroCard |
| `icon_tab_alerts_active.svg` | `#1884DC` `#174E86` `#36C09B` | BottomNavBar |
| `icon_tab_alerts_unread.svg` | `#F0A92E` | StatusHeroCard |
| `icon_tab_family.svg` | `#939DAA` | BottomNavBar, CardiMemberDetailPage |
| `icon_tab_family_active.svg` | `#174E86` `#1884DC` `#36C09B` | BottomNavBar |
| `icon_tab_home.svg` | `#939DAA` | BottomNavBar |
| `icon_tab_home_active.svg` | `#FFFFFF` `#174E86` `#1884DC` `#36C09B` | BottomNavBar |
| `icon_tab_journal.svg` | `#939DAA` | BottomNavBar, StatusHeroCard |
| `icon_tab_journal_active.svg` | `#174E86` `#1884DC` `#36C09B` | BottomNavBar |
| `icon_tab_journal_primary.svg` | `#1884DC` `#FFFFFF` | StatusHeroCard |
| `icon_tab_qa.svg` | `#939DAA` | StatusHeroCard |
| `icon_tab_qa_primary.svg` | `#3175B9` `#153D66` `#FFFFFF` | StatusHeroCard |
| `icon_tab_settings.svg` | `#939DAA` | BottomNavBar |
| `icon_tab_settings_active.svg` | `#174E86` `#1884DC` `#36C09B` | BottomNavBar |

### Status and severity

| Icon | Colours | Used by |
| --- | --- | --- |
| `icon_caution_danger.svg` | `#FBE4E4` `#C42F2F` | **nothing** |
| `icon_status_check.svg` | `#E4F6F2` `#1F8A72` | ConnectionSuccessPage, DashboardPage |
| `icon_status_critical.svg` | `#FBE4E4` `#C42F2F` | **nothing** |
| `icon_status_info_green.svg` | `#1F8A72` | StatusHeroCard |
| `icon_status_info_orange.svg` | `#B45309` | StatusHeroCard |
| `icon_status_info_red.svg` | `#C42F2F` | StatusHeroCard |
| `icon_status_info_yellow.svg` | `#A9741A` | StatusHeroCard |
| `icon_status_paused.svg` | `#EEF1F5` `#727272` | StatusHeroCard |
| `icon_status_urgent.svg` | `#FCEDE2` `#B45309` | **nothing** |
| `icon_status_warning.svg` | `#FFF3DE` `#A9741A` | AlertMiniCard |
| `icon_trend_good.svg` | `#1F8A72` | MovementCards |
| `icon_trend_neutral.svg` | `#727272` | MovementCards |
| `icon_trend_watch.svg` | `#153D66` | CardiMemberDetailPage |

### Metrics

| Icon | Colours | Used by |
| --- | --- | --- |
| `icon_metric_breathing.svg` | `#3E8AC7` | AlertDetailPage, MetricCard, TrendMetricCatalogue |
| `icon_metric_heart.svg` | `#E53E3E` | AlertDetailPage, AlertMiniCard, BaselineLearningPage, ConnectionSuccessPage, DeviceConnectionPage, MetricCard, TrendMetricCatalogue |
| `icon_metric_sleep.svg` | `#7C6FDC` | AlertDetailPage, AlertMiniCard, DeviceConnectionPage, MetricCard, TrendMetricCatalogue |
| `icon_metric_spo2.svg` | `#1F8A72` | MetricCard, TrendMetricCatalogue |
| `icon_metric_steps.svg` | `#3175B9` | AlertDetailPage, AlertMiniCard, ConnectionSuccessPage, DeviceConnectionPage, MetricCard, TrendMetricCatalogue |
| `icon_metric_temperature.svg` | `#1F8A72` | MetricCard, TrendMetricCatalogue |

### Actions

| Icon | Colours | Used by |
| --- | --- | --- |
| `icon_action_call.svg` | `#153D66` `#3175B9` | AlertListCard, CardiMemberDetailPage, QuickActionRow |
| `icon_action_check.svg` | `#153D66` | AcceptInvitePage, AlertListCard, ChoiceSheetPage, FamilySwitcherPage, FindingsList, InviteWaitPage |
| `icon_action_details.svg` | `#3175B9` `#153D66` | DashboardPage |
| `icon_action_profile.svg` | `#3175B9` `#153D66` | QuickActionRow |
| `icon_action_sms.svg` | `#3175B9` `#153D66` `#FFFFFF` | CardiMemberDetailPage, QuickActionRow |
| `icon_action_sos.svg` | `#861B1B` `#C42F2F` | QuickActionRow |

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
| `icon_back_white.svg` | `#FFFFFF` | AcceptInvitePage, AlertDetailPage, AlertRespondPage, AlertSettingsPage, AlertsPage, ApproveJoinRequestPage, CardiMemberDetailPage, CaregiverInvitesPage, DeviceManagementPage, EditCardiMemberPage, ExportConsentsPage, ExportHealthDataPage, FamilyPage, JoinFamilyPage, JournalEntryPage, JournalPage, JournalTimingPage, LegalDocumentPage, MedicalInformationPage, MemberChatPage, MetricAlarmEditPage, MetricAlarmsPage, MetricTrendPage, NotificationPreferencesPage, NotificationsPage, QuestionnairesPage, SettingsPage, StartFamilyPage, TransferFamilyAdminPage, WizardHeader |
| `icon_caret_down.svg` | `#343434` | FilterChipBar |
| `icon_caret_down_white.svg` | `#FFFFFF` | FamilyPage, FilterChipBar, MemberChatPage |
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
| `icon_advise_primary.svg` | `#3175B9` `#153D66` `#FFFFFF` | StatusHeroCard |
| `icon_bell.svg` | `#3175B9` `#153D66` | CardiMemberDetailPage, DashboardHeader, DashboardPage |
| `icon_bell_cog.svg` | `#1884DC` `#D9DCE1` `#727272` | CardiMemberDetailPage |
| `icon_binoculars.svg` | `#153D66` `#3175B9` | CardiMemberDetailPage |
| `icon_book.svg` | `#3175B9` `#153D66` | CardiMemberDetailPage |
| `icon_book_cog.svg` | `#1884DC` `#D9DCE1` `#727272` | CardiMemberDetailPage |
| `icon_calendar.svg` | `#939DAA` | DateField, DeviceCard |
| `icon_callout.svg` | `#3175B9` `#153D66` `#FFFFFF` | CardiMemberDetailPage |
| `icon_chatbot.svg` | `#36C09B` `#1884DC` `#174E86` `#9FEBFF` `#B9F1FF` `#123A5F` `#FFFFFF` | ChatBotLauncher, MemberChatPage, PendingBotIndicator |
| `icon_chatbot_mono.svg` | `#153D66` `#3175B9` | CardiMemberDetailPage |
| `icon_check_disc.svg` | `#3175B9` `#153D66` `#FFFFFF` | BaselineLearningPage, FindingsList |
| `icon_check_white.svg` | `#FFFFFF` | AlertDetailPage |
| `icon_clipboard.svg` | `#3175B9` `#153D66` | CardiMemberDetailPage, InviteWaitPage |
| `icon_clipboard_ink.svg` | `#D9DCE1` `#727272` | CardiMemberDetailPage |
| `icon_edit.svg` | `#153D66` `#3175B9` | CardiMemberDetailPage, MedicalInformationPage |
| `icon_expand.svg` | `#153D66` | MetricTrendCard |
| `icon_export_white.svg` | `#FFFFFF` | ExportAction |
| `icon_eye.svg` | `#939DAA` | CreateAccountPage, SignInPage |
| `icon_eye_off.svg` | `#939DAA` | CreateAccountPage, SignInPage |
| `icon_format_csv.svg` | `#3175B9` `#153D66` `#FFFFFF` | ExportFormatPopupPage |
| `icon_format_pdf.svg` | `#FBE4E4` `#C42F2F` | ExportFormatPopupPage |
| `icon_history_white.svg` | `#FFFFFF` | MemberChatPage |
| `icon_home_white.svg` | `#FFFFFF` | DashboardHeader, WizardHeader |
| `icon_marker_square.svg` | `#C42F2F` `#861B1B` | FindingsList |
| `icon_medical_files.svg` | `#153D66` `#3175B9` | CardiMemberDetailPage |
| `icon_moon.svg` | `#3175B9` `#153D66` | FamilyPage |
| `icon_movement_attention.svg` | `#FBE4E4` `#C42F2F` | MovementCards |
| `icon_notification.svg` | `#FFFFFF` | FcmNotificationChannel, PushRegistrationCoordinator |
| `icon_open.svg` | `#153D66` | ExportDeliveryPopupPage |
| `icon_pause.svg` | `#B45309` | CardiMemberDetailPage |
| `icon_person_remove.svg` | `#FBE4E4` `#C42F2F` | CardiMemberDetailPage, FamilyPage |
| `icon_plus.svg` | `#153D66` | ConnectionSuccessPage, DeviceManagementPage |
| `icon_power_red.svg` | `#C42F2F` | SettingsPage |
| `icon_power_white.svg` | `#FFFFFF` | DashboardPage, SettingsPage |
| `icon_refresh.svg` | `#939DAA` | AlertsPage, ConnectionSuccessPage, DeviceCard |
| `icon_refresh_white.svg` | `#FFFFFF` | AlertsPage |
| `icon_save.svg` | `#3175B9` `#153D66` `#C9E1FF` `#FFFFFF` | ExportDeliveryPopupPage |
| `icon_share.svg` | `#153D66` | ExportDeliveryPopupPage, InviteWaitPage |
| `icon_star.svg` | `#939DAA` | DeviceCard, StarRatingView |
| `icon_trash.svg` | `#FBE4E4` `#C42F2F` | AlertDetailPage, AlertListCard, DeviceCard, MetricAlarmEditPage, QuestionCard |
| `icon_user_bell.svg` | `#1884DC` `#D9DCE1` `#727272` | CardiMemberDetailPage |
| `icon_watch.svg` | `#D9DCE1` `#727272` `#FFFFFF` | CardiMemberDetailPage |

## How "used by" is decided

Every `.xaml` and `.cs` under `src/` is searched for the file name as a literal, which is
how icons are referenced throughout — `Source="icon_x.svg"`, or a string constant as in
`FindingsList.CheckMarker`. A name mentioned only in a comment therefore counts as a use, so
a row claiming a single caller is worth reading before trusting. The one runtime-built name,
the bottom nav's `{stem}_active.svg`, is matched on its stem.

## How the files are made

Everything except the vendor marks, the splash gradient, the Android notification icon, the
240dp chat launcher illustration (`icon_chatbot.svg`) and the bottom-nav tab icons is generated
by `scripts/icons/generate.mjs` from `scripts/icons/icons.json`,
which holds the icon name, source (IconPark or Material Symbols), colour group and size per
file. Edit the JSON, run `npm ci && npm run generate` in `scripts/icons`, then regenerate this file.
