# Family sharing — what the screens look like

Taken on the `Pixel_9_Pro` emulator against the dev API on 2026-09-22, at half the device's
1280×2856 and then halved again for the repo. These are the screens the PRD calls **needs design
sync**: none of them has a Figma frame, so until one exists this folder is the only record of what
shipped, and a reviewer of the design work has something to disagree with.

Re-take them with the [Android emulator runbook](../../../../technical/android_emulator_runbook.md);
replace a file in place rather than adding a second copy with a date on it.

| File | Screen | What it shows |
|---|---|---|
| `family-tab-admin.png` | Family tab, admin state | The role card, the night-cover band D-8 wrote, the roster, and who the family watches with each member's open alert |
| `family-switcher-drawer.png` | Switcher drawer (D-19) | One row per family with its worst open alert, the current one ticked, and the two ways to another family at the foot |
| `member-management-rows.png` | Member Details | "Who can see them" among the management rows |
| `who-can-see.png` | Who can see &lt;name&gt; | The invitations for one member, empty, with what an invitation grants |
| `invitation-link.png` | Who can see &lt;name&gt; | A freshly minted invitation: the link, its QR code, its deadline, and the row it added |
| `accept-invitation.png` | Accept an invitation | Who invited whom, first names only, read at render time |
| `accept-quiet-hours-choice.png` | Accept an invitation | The forced quiet-hours question (D-8) answered, which is what enables Accept |
| `alert-respond.png` | Answer an alert | The rule's canned chips, the note and its 500-character count |
| `alert-what-the-family-did.png` | Alert detail | The response list, the attribution line, and Close beside Undo |
| `notification-preferences.png` | Notifications | The escalated-alert preference, in the section shape Alert Settings uses |

The Family ID card is absent from `family-tab-admin.png`: `FamilySummary.familyId` ships in the
same change as these screens and the dev API had not been redeployed when they were taken. The card
draws when the field arrives, and hides rather than showing an empty line when it does not.
