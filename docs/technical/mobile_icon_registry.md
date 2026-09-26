# Mobile icon registry

Every SVG in `src/Presentation/CardiTrack.Mobile/Resources/Images`, what uses it, and every
colour it hard-codes. Written so a redesign can be scoped without grepping: the question
"what breaks if this icon changes" has an answer here.

**Generated, not hand-maintained.** Regenerate with:

```bash
python scripts/generate-icon-registry.py
```

A row that disagrees with the code means this file is stale, not that the code is wrong.

**151 icons**, **40 distinct colours**, **8 referenced nowhere**.

## Why the colours matter

Every icon hard-codes its own hex. None read `Colors.xaml`, because MAUI cannot tint an
`<Image>` source from a resource — so a palette change is a change to this many files, not
to one. That is the largest cost in any redesign of this set, and the reason this table
lists every colour rather than only the common ones: a colour used once still pins a file.

| Colour | Icons | Also known as |
| --- | --- | --- |
| `#FFFFFF` | 44 | White |
| `#153D66` | 28 | icons.json brand outer stroke |
| `#3175B9` | 22 | icons.json brand and activity fill, steel blue |
| `#174E86` | 21 | PrimaryDark |
| `#939DAA` | 17 | MutedText |
| `#C42F2F` | 15 | DangerRed |
| `#1884DC` | 12 | Primary |
| `#1F8A72` | 9 | MetricTemperatureInk / DatasetBodyText |
| `#36C09B` | 8 | StatusGreen |
| `#10659F` | 7 | DatasetActivityText |
| `#727272` | 7 | Body / Body2 ink |
| `#FBE4E4` | 7 | derived: StatusRed at 14% over white |
| `#D9DCE1` | 5 | — |
| `#B45309` | 4 | severity orange ink |
| `#123A5F` | 2 | — |
| `#5A3300` | 2 | — |
| `#861B1B` | 2 | — |
| `#9FEBFF` | 2 | — |
| `#A9741A` | 2 | DatasetWarningText |
| `#B9F1FF` | 2 | — |
| `#E4F6F2` | 2 | DatasetBodyBackground |
| `#1A6CB0` | 1 | — |
| `#2FA6C4` | 1 | — |
| `#343434` | 1 | Body2Dark ink |
| `#34A853` | 1 | — |
| `#3E8AC7` | 1 | MetricBreathingInk |
| `#4285F4` | 1 | — |
| `#5A4EBF` | 1 | DatasetSleepText |
| `#5C6672` | 1 | — |
| `#7C6FDC` | 1 | MetricSleepInk |
| `#B93A55` | 1 | DatasetHeartText |
| `#C9E1FF` | 1 | MetricTileTint, opaque (brand internal fill) |
| `#E53E3E` | 1 | ErrorRed |
| `#EA4335` | 1 | — |
| `#EEF1F5` | 1 | DatasetOtherBackground |
| `#EF9F27` | 1 | — |
| `#F0A92E` | 1 | warning amber |
| `#FBBC05` | 1 | — |
| `#FCEDE2` | 1 | derived: StatusOrange at 14% over white |
| `#FFF3DE` | 1 | DatasetWarningBackground |

## Unreferenced

In the tree, used by nothing. Candidates for deletion — but check the history first: an
icon can be staged ahead of a screen that has not shipped.

- `icon_btn_history.svg`
- `icon_btn_refresh.svg`
- `icon_caret_down.svg`
- `icon_caution_danger.svg`
- `icon_history_white.svg`
- `icon_power_red.svg`
- `icon_status_critical.svg`
- `icon_status_urgent.svg`

## The set

### Tab bar

| Icon | Colours | Used by |
| --- | --- | --- |
| `icon_tab_alerts.svg` | `#939DAA` | BottomNavBar, ChatSuggestionGlyph, StatusHeroCard |
| `icon_tab_alerts_active.svg` | `#1884DC` `#174E86` `#36C09B` | BottomNavBar |
| `icon_tab_alerts_unread.svg` | `#F0A92E` | StatusHeroCard |
| `icon_tab_family.svg` | `#939DAA` | BottomNavBar, CardiMemberDetailPage |
| `icon_tab_family_active.svg` | `#174E86` `#1884DC` `#36C09B` | BottomNavBar |
| `icon_tab_home.svg` | `#939DAA` | BottomNavBar |
| `icon_tab_home_active.svg` | `#FFFFFF` `#174E86` `#1884DC` `#36C09B` | BottomNavBar |
| `icon_tab_journal.svg` | `#939DAA` | BottomNavBar, ChatSuggestionGlyph, StatusHeroCard |
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
| `icon_status_check.svg` | `#E4F6F2` `#1F8A72` | ConnectionSuccessPage, MemberChatPage, MemberDashboardCard |
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
| `icon_metric_spo2.svg` | `#1F8A72` | AlertDetailPage, MetricCard, TrendMetricCatalogue |
| `icon_metric_steps.svg` | `#3175B9` | AlertDetailPage, AlertMiniCard, ConnectionSuccessPage, DeviceConnectionPage, MetricCard, TrendMetricCatalogue |
| `icon_metric_temperature.svg` | `#1F8A72` | MetricCard, TrendMetricCatalogue |

### Actions

| Icon | Colours | Used by |
| --- | --- | --- |
| `icon_action_call.svg` | `#153D66` `#3175B9` | AlertListCard, CardiMemberDetailPage, QuickActionRow |
| `icon_action_check.svg` | `#153D66` | AcceptInvitePage, AlertListCard, FamilySwitcherPage, FindingsList, InviteWaitPage |
| `icon_action_details.svg` | `#3175B9` `#153D66` | MemberDashboardCard |
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
| `icon_caret_down.svg` | `#343434` | **nothing** |
| `icon_caret_down_white.svg` | `#FFFFFF` | FamilyPage, MemberChatPage |
| `icon_chevron.svg` | `#939DAA` | AlertDetailPage, CardiMemberDetailPage, CompleteThePictureCard, FamilyPage, MemberChatPage |
| `icon_chevron_down.svg` | `#939DAA` | AccordionSection, AlertListCard, ChoiceField, DeviceManagementPage, MedicalInformationPage, MemberDashboardCard |
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
| `icon_battery_full.svg` | `#1F8A72` | DeviceCard |
| `icon_battery_half.svg` | `#1F8A72` | DeviceCard |
| `icon_battery_low.svg` | `#C42F2F` | DeviceCard |
| `icon_bell.svg` | `#3175B9` `#153D66` | DashboardPage |
| `icon_bell_cog.svg` | `#1884DC` `#D9DCE1` `#727272` | CardiMemberDetailPage |
| `icon_binoculars.svg` | `#153D66` `#3175B9` | CardiMemberDetailPage |
| `icon_book.svg` | `#3175B9` `#153D66` | CardiMemberDetailPage |
| `icon_book_cog.svg` | `#1884DC` `#D9DCE1` `#727272` | CardiMemberDetailPage |
| `icon_btn_close.svg` | `#FFFFFF` | ActionLook, AlertRespondPage, AppPasswordPage, ContactEditPopupPage, MedicalEntryEditPopupPage, MedicalSortPopupPage, QuestionCard |
| `icon_btn_connect.svg` | `#FFFFFF` | ActionLook, DeviceManagementPage, NoDevicePopupPage |
| `icon_btn_connect_tint.svg` | `#10659F` | DeviceCard |
| `icon_btn_copy_tint.svg` | `#10659F` | FamilyPage |
| `icon_btn_delete.svg` | `#FFFFFF` | ActionLook, AlertDetailPage, MemberChatPage, MetricAlarmEditPage |
| `icon_btn_delete_tint.svg` | `#C42F2F` | DeviceCard |
| `icon_btn_edit.svg` | `#FFFFFF` | ActionLook |
| `icon_btn_history.svg` | `#FFFFFF` | **nothing** |
| `icon_btn_history_tint.svg` | `#10659F` | ChatSuggestionGlyph, DeviceCard |
| `icon_btn_later.svg` | `#FFFFFF` | ActionLook, NoDevicePopupPage |
| `icon_btn_plus.svg` | `#FFFFFF` | ActionLook, MedicalInformationPage |
| `icon_btn_plus_tint.svg` | `#10659F` | ConnectionSuccessPage, MemberChatPage |
| `icon_btn_refresh.svg` | `#FFFFFF` | **nothing** |
| `icon_btn_refresh_tint.svg` | `#10659F` | DeviceCard |
| `icon_btn_reset.svg` | `#FFFFFF` | FilterSheetPage |
| `icon_btn_resolve.svg` | `#FFFFFF` | ActionLook, AlertDetailPage, AlertRespondPage, FilterSheetPage, MedicalInformationPage |
| `icon_btn_save.svg` | `#FFFFFF` | ActionLook, ContactEditPopupPage, MedicalEntryEditPopupPage, MedicalSortPopupPage, QuestionCard |
| `icon_btn_share.svg` | `#FFFFFF` | FamilyPage |
| `icon_btn_sort.svg` | `#5A3300` | MedicalInformationPage |
| `icon_btn_undo.svg` | `#5A3300` | ActionLook |
| `icon_btn_undo_tint.svg` | `#10659F` | AlertDetailPage |
| `icon_calendar.svg` | `#939DAA` | DateField |
| `icon_callout.svg` | `#3175B9` `#153D66` `#FFFFFF` | CardiMemberDetailPage |
| `icon_chatbot.svg` | `#36C09B` `#1884DC` `#174E86` `#9FEBFF` `#B9F1FF` `#123A5F` `#FFFFFF` | ChatBotLauncher, EcgTraceView, MemberChatPage, PendingBotIndicator |
| `icon_chatbot_body.svg` | `#36C09B` `#1884DC` `#174E86` `#9FEBFF` `#B9F1FF` `#123A5F` | MemberChatPage |
| `icon_chatbot_mono.svg` | `#153D66` `#3175B9` | CardiMemberDetailPage |
| `icon_check_disc.svg` | `#3175B9` `#153D66` `#FFFFFF` | BaselineLearningPage, FindingsList |
| `icon_check_white.svg` | `#FFFFFF` | ActionLook, AlertDetailPage, AlertRespondPage, AppPasswordPage, ChoiceSheetPage |
| `icon_clipboard.svg` | `#3175B9` `#153D66` | CardiMemberDetailPage, InviteWaitPage |
| `icon_clipboard_ink.svg` | `#D9DCE1` `#727272` | CardiMemberDetailPage |
| `icon_dataset_activity.svg` | `#10659F` | ChatSuggestionGlyph, DeviceCard |
| `icon_dataset_body.svg` | `#1F8A72` | DeviceCard |
| `icon_dataset_heart.svg` | `#B93A55` | ChatSuggestionGlyph, DeviceCard |
| `icon_dataset_other.svg` | `#5C6672` | DeviceCard |
| `icon_dataset_sleep.svg` | `#5A4EBF` | ChatSuggestionGlyph, DeviceCard |
| `icon_device_off.svg` | `#C42F2F` | StatusHeroCard |
| `icon_edit.svg` | `#153D66` `#3175B9` | CardiMemberDetailPage |
| `icon_expand.svg` | `#153D66` | MetricTrendCard |
| `icon_export.svg` | `#174E86` | JournalPage |
| `icon_export_white.svg` | `#FFFFFF` | ExportAction |
| `icon_eye.svg` | `#939DAA` | CreateAccountPage, SignInPage |
| `icon_eye_off.svg` | `#939DAA` | CreateAccountPage, SignInPage |
| `icon_filter.svg` | `#174E86` `#FFFFFF` | AlertsPage, JournalPage |
| `icon_filter_white.svg` | `#FFFFFF` `#174E86` | AlertsPage, JournalPage |
| `icon_format_csv.svg` | `#E4F6F2` `#1F8A72` | ExportFormatPopupPage |
| `icon_format_pdf.svg` | `#FBE4E4` `#C42F2F` | ExportFormatPopupPage |
| `icon_header_bell.svg` | `#174E86` | CardiMemberDetailPage, DashboardHeader |
| `icon_header_edit.svg` | `#174E86` | CardiMemberDetailPage |
| `icon_header_history.svg` | `#174E86` | MemberChatPage |
| `icon_header_plus.svg` | `#174E86` | DeviceManagementPage |
| `icon_header_save.svg` | `#174E86` | EditCardiMemberPage |
| `icon_help.svg` | `#3175B9` `#153D66` `#FFFFFF` | DeviceManagementPage |
| `icon_history_white.svg` | `#FFFFFF` | **nothing** |
| `icon_home_white.svg` | `#FFFFFF` | AccountSetupPage, DashboardHeader, WizardHeader |
| `icon_ledger_allergy.svg` | `#FBE4E4` `#C42F2F` | MedicalLedgerLines |
| `icon_ledger_condition.svg` | `#153D66` | MedicalLedgerLines |
| `icon_ledger_medication.svg` | `#3175B9` `#153D66` `#FFFFFF` | MedicalLedgerLines |
| `icon_ledger_other.svg` | `#3175B9` `#153D66` `#FFFFFF` | MedicalLedgerLines |
| `icon_marker_square.svg` | `#C42F2F` `#861B1B` | FindingsList |
| `icon_medical_files.svg` | `#153D66` `#3175B9` | CardiMemberDetailPage |
| `icon_moon.svg` | `#3175B9` `#153D66` | FamilyPage |
| `icon_more_vertical.svg` | `#939DAA` | MedicalInformationPage |
| `icon_movement_attention.svg` | `#FBE4E4` `#C42F2F` | MovementCards |
| `icon_notification.svg` | `#FFFFFF` | FcmNotificationChannel, PushRegistrationCoordinator |
| `icon_open.svg` | `#153D66` | ExportDeliveryPopupPage |
| `icon_pause.svg` | `#B45309` | CardiMemberDetailPage |
| `icon_person_remove.svg` | `#FBE4E4` `#C42F2F` | CardiMemberDetailPage, FamilyPage |
| `icon_pin.svg` | `#939DAA` | StatusHeroCard |
| `icon_pin_on.svg` | `#3175B9` `#153D66` | StatusHeroCard |
| `icon_plus.svg` | `#153D66` | FamilyPage |
| `icon_power_red.svg` | `#C42F2F` | **nothing** |
| `icon_power_white.svg` | `#FFFFFF` | DashboardPage, SettingsPage |
| `icon_refresh.svg` | `#939DAA` | AlertsPage, ConnectionSuccessPage |
| `icon_refresh_white.svg` | `#FFFFFF` | AlertsPage |
| `icon_save.svg` | `#3175B9` `#153D66` `#C9E1FF` `#FFFFFF` | ExportDeliveryPopupPage |
| `icon_section_about.svg` | `#174E86` | SettingsPage |
| `icon_section_account.svg` | `#174E86` | SettingsPage |
| `icon_section_delete.svg` | `#C42F2F` | SettingsPage |
| `icon_section_muted.svg` | `#174E86` | SettingsPage |
| `icon_section_notifications.svg` | `#174E86` | SettingsPage |
| `icon_section_privacy.svg` | `#174E86` | SettingsPage |
| `icon_share.svg` | `#153D66` | ExportDeliveryPopupPage, InviteWaitPage |
| `icon_star.svg` | `#939DAA` | StarRatingView |
| `icon_star_off.svg` | `#939DAA` | DeviceCard |
| `icon_star_on.svg` | `#EF9F27` `#B45309` | DeviceCard |
| `icon_trash.svg` | `#FBE4E4` `#C42F2F` | AlertListCard, QuestionCard |
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
