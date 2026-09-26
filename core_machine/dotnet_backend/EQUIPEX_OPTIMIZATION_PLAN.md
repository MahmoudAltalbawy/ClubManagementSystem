# Equipex Flutter App — Full Optimization Plan

**Scope:** 104 Dart files across 25+ modules analyzed page-by-page.
**Goal:** Make every interaction silky-smooth — keyboard, transitions, button feedback, scrolling — without changing any visual style or appearance.

---

## Table of Contents

1. [Global Foundation Layer](#1-global-foundation-layer)
2. [AppBar & Background Consistency System](#2-appbar--background-consistency-system)
3. [Keyboard Smoothness System](#3-keyboard-smoothness-system)
4. [Page Transition Animation System](#4-page-transition-animation-system)
5. [Universal Button Animation System](#5-universal-button-animation-system)
6. [Page-by-Page Optimization](#6-page-by-page-optimization)
7. [BLoC Layer Optimizations](#7-bloc-layer-optimizations)
8. [Scroll & List Performance](#8-scroll--list-performance)
9. [Implementation Priority & Order](#9-implementation-priority--order)

---

## 1. Global Foundation Layer

These changes create the infrastructure that every page benefits from. They are done once and propagated everywhere.

### 1.1 Create `core/smooth_keyboard_mixin.dart`

**What:** A reusable mixin that every page with text fields will use.
**Why:** Right now, 14 different views each implement their own keyboard-avoidance logic — some use `resizeToAvoidBottomInset: false` with `MediaQuery.viewInsetsOf`, others use `AnimatedPadding` with different durations (120ms, 180ms), and the `AskEqiupeIoView` doesn't dispose its controllers at all. This inconsistency creates jarring differences when switching between pages.

**Details:**
- Create a `SmoothKeyboardMixin` on `State<T>` that:
  - Listens to `WidgetsBindingObserver.didChangeMetrics` for keyboard height changes.
  - Exposes a `keyboardHeight` getter with smooth interpolation (use a `ValueNotifier<double>` driven by a short animation curve).
  - Provides a `keyboardPadding` widget that wraps content with an `AnimatedPadding` using a consistent **200ms easeOutCubic** curve everywhere.
  - Auto-scrolls the focused field into view when the keyboard appears (using `Scrollable.ensureVisible` with a 300ms duration and `easeOutCubic` curve).
  - Automatically unfocuses when tapping outside text fields (via a `GestureDetector` with `HitTestBehavior.translucent`).
- Every view that currently uses `resizeToAvoidBottomInset: false` will switch to this mixin.

**Files affected (14 views):**
`LoginView.dart`, `SignUpView.dart`, `CompleteProfileView.dart`, `AddTeamView.dart`, `AddClubView.dart`, `AddEventView.dart`, `AddMembersView.dart`, `AddAnnouncementView.dart`, `AddPlansView.dart`, `AddMedicalRecordView.dart`, `SearchView.dart`, `ChatView.dart`, `AskEqiupeIoView.dart`, `GameDetailHistoryView.dart`

### 1.2 Create `core/animated_button.dart`

**What:** A universal `AnimatedButton` wrapper widget that replaces every raw `ElevatedButton`, `OutlinedButton`, `TextButton`, `FilledButton`, and `IconButton` in the app.
**Why:** Currently zero buttons in the entire app have press feedback animation. The only tap animation is `AnimatedPressable` in `app_transitions.dart`, which is used on only ~8 cards across `HomeView` and `DayEventsDetailView`. Every other tappable element is static — tapping a "Save" button, a "Send Invitation" button, or a navigation icon gives no physical feedback. This makes the app feel unresponsive.

**Details:**
- Wrap every button in a `ScaleTransition` that:
  - On `onTapDown`: scales to **0.95** over **80ms** with `easeOutCubic`.
  - On `onTapUp` / `onTapCancel`: springs back to **1.0** over **160ms** with `easeOutBack` (slight overshoot for bounce feel).
  - Uses a `SingleTickerProviderStateMixin` controller.
- For `IconButton` specifically (like nav icons, edit icons, close icons): use a subtler **0.85** scale with a **60ms** down and **140ms** up.
- For FAB actions in `MainNavigation`: the existing `AnimatedPressable` already handles this — keep it, but make its spring-back curve `easeOutBack` instead of the current linear reverse.

**Implementation approach:** Create `AnimatedButton` as a wrapper that takes a `child` (the existing button) and adds the scale animation via `GestureDetector` and `AnimatedBuilder`. This means **zero style changes** — the button looks identical, it just responds to touch.

### 1.3 Upgrade `AppPageRoute` in `core/app_transitions.dart`

**What:** Enhance the existing page transition with a smoother, more polished feel.
**Why:** The current `AppPageRoute` uses a 320ms slide+fade, which is functional but abrupt. The slide offset of `0.22` is a bit harsh. The outgoing page slides to `Offset(-0.08, 0)` which is barely noticeable. The `AppFadeRoute` at 450ms is too slow for most transitions.

**Details:**
- **AppPageRoute** changes (keep the same class, just adjust parameters):
  - Duration: **320ms** stays (this is good).
  - Incoming slide: change from `Offset(0.22, 0)` to `Offset(0.15, 0)` — a smaller, more iOS-like slide distance.
  - Incoming opacity: change from `0.94 -> 1.0` to `0.0 -> 1.0` over the first 40% of the animation (using `Interval(0.0, 0.4)`) — this creates a subtle "materialize" effect.
  - Outgoing slide: change from `Offset(-0.08, 0)` to `Offset(-0.06, 0)` with a `FadeTransition` from `1.0` to `0.85` — the previous page gently dims and slides slightly left.
  - Curve: change from `fastOutSlowIn` to `Curves.easeOutExpo` for the incoming, `Curves.easeInCubic` for the outgoing — sharper start, softer landing.
- **AppFadeRoute** changes:
  - Duration: reduce from **450ms** to **350ms**.
  - Add a very subtle `ScaleTransition` from `0.97` to `1.0` alongside the fade — this prevents the "flat pop-in" feeling.
- Both routes already dismiss the keyboard in `didPush`/`didPop` — keep this.

### 1.4 Extract `AppBackground` usage everywhere

**What:** Replace the 25+ duplicated `Container(decoration: BoxDecoration(gradient: ..., image: ...))` blocks with the existing `AppBackground` widget from `core/app_background.dart`.
**Why:** Every single view manually constructs the same gradient + `DecorationImage(assets/background.png, opacity: 0.15)`. This means 25+ `BoxDecoration` objects are created on every build of every page. While individually cheap, `AppBackground` exists and isn't being used — using it eliminates the duplication and ensures any future background change happens in one place.

**Files affected:** Every view file in the app (all 25+ page files).

---

## 2. AppBar & Background Consistency System

During the full analysis, one of the most noticeable inconsistencies across the app is that some pages have scrollable AppBars that collapse, others have fixed AppBars, and the background behavior (gradient + background.png) varies from page to page — sometimes it scrolls with content, sometimes it's pinned. This section standardizes everything so every screen in the app feels like it belongs to the same app.

### 2.1 Current AppBar Inconsistency Audit

| Page | AppBar Type | Scrolls? | Background Behavior |
|---|---|---|---|
| HomeView | None (no AppBar) | N/A | Fixed (Container wraps everything) |
| EventView | None (no AppBar) | N/A | Fixed |
| TeamView | None (no AppBar) | N/A | Fixed |
| ProfileView | None (no AppBar) | N/A | Fixed |
| MessagesView | None (no AppBar) | N/A | Fixed |
| ChatView | Custom inline AppBar | Fixed | Fixed |
| SearchView | None | N/A | Fixed |
| DayEventsDetailView | CustomAppBar (via Scaffold.appBar) | Fixed | Fixed |
| MemberDetailView | CustomAppBar | Fixed | Fixed |
| PlayerProfileView | CustomAppBar | Fixed | Fixed |
| MedicalRecordView | CustomAppBar | Fixed | Fixed |
| MedicalRecordDetailView | CustomAppBar | Fixed | Fixed (but uses `SizedBox.expand`) |
| AddMedicalRecordView | CustomAppBar | Fixed | Fixed (but uses `SizedBox.expand`) |
| SettingsView | CustomAppBar | Fixed | Fixed |
| NotificationsView | CustomAppBar | Fixed | Fixed (but ListView top-padding manually accounts for AppBar height) |
| PlansView | None (embedded in TeamView TabBarView) | N/A | Transparent scaffold |
| TeamStatsView | None (embedded in TeamView TabBarView) | N/A | Transparent scaffold |
| AddTeamView | CustomAppBar | Fixed | Fixed |
| AddClubView | CustomAppBar | Fixed | Fixed |
| AddEventView | CustomAppBar | Fixed | Fixed |
| AddMembersView | CustomAppBar | Fixed | Fixed |
| AddAnnouncementView | CustomAppBar | Fixed | Fixed |
| AddPlansView | CustomAppBar | Fixed | Fixed |
| JoinTeamView | CustomAppBar | Fixed | Fixed |
| IncomingRequestsView | CustomAppBar | Fixed | Fixed |
| GameSquadView | CustomAppBar | Fixed | Fixed |
| MatchDetailView | CustomAppBar | Fixed, `extendBodyBehindAppBar: true` | Body extends behind AppBar |
| GameDetailHistoryView | CustomAppBar | Fixed, `extendBodyBehindAppBar: true` | Body extends behind AppBar |
| CongratsView | None | N/A | Fixed with dark overlay |
| AskEqiupeIoView | None (SizedBox spacer instead) | N/A | Fixed |
| UploadPdfView | CustomAppBar | Fixed | Fixed |
| UploadVideoView | CustomAppBar | Fixed | Fixed |
| LoginView | No scaffold AppBar (inline back button) | N/A | Fixed (auth gradient) |
| SignUpView | No scaffold AppBar (inline back button) | N/A | Fixed (auth gradient) |
| CompleteProfileView | No scaffold AppBar (inline back button) | N/A | Fixed (auth gradient) |

### 2.2 The Problems

**Problem 1 — `extendBodyBehindAppBar` inconsistency:** `MatchDetailView` and `GameDetailHistoryView` use `extendBodyBehindAppBar: true`, which means the body content renders behind the AppBar. Every other page uses the default `false`. This creates a visual jump when navigating between these pages and the rest of the app — the content area "shifts" up/down as the AppBar presence changes.

**Problem 2 — NotificationsView manual padding:** `NotificationsView` uses `ListView.builder` with `padding: EdgeInsets.only(top: appBarHeight + ...)` to manually account for the AppBar. This is fragile — if the AppBar height changes (e.g., team switcher appears/disappears), the padding is wrong and content overlaps or has a gap.

**Problem 3 — Background scroll behavior:** In all pages, the background (gradient + background.png) is applied to a `Container` that wraps the entire body. Because the `Container` is inside the `SingleChildScrollView` in some views but outside it in others, the background sometimes scrolls with content (bad — the gradient shifts) and sometimes stays fixed (good).

**Problem 4 — `SizedBox.expand` vs plain Container:** Some views use `SizedBox.expand > Container(decoration)`, others use just `Container(decoration)` with `width/height: double.infinity`. These behave identically but the inconsistency makes the codebase harder to maintain.

**Problem 5 — Auth pages have inline back buttons:** `LoginView`, `SignUpView`, and `CompleteProfileView` don't use `CustomAppBar` — they have their own inline `IconButton` back buttons positioned differently from the rest of the app.

### 2.3 The Standardization Plan

**Rule 1 — Every page uses `CustomAppBar` through Scaffold.appBar (except the 5 main nav tabs and auth flow):**
- The 5 main tabs (Home, Events, Team, Profile, Messages) are managed by `MainNavigation` which has its own AppBar arrangement — leave these as-is since they share a common structure.
- Auth pages (Login, SignUp, CompleteProfile) have a deliberately different visual style — leave their inline back buttons but standardize their positioning.
- Every other page MUST use `Scaffold(appBar: CustomAppBar(...))` with consistent parameters.

**Rule 2 — Never use `extendBodyBehindAppBar: true`:**
- Remove it from `MatchDetailView` and `GameDetailHistoryView`.
- Reason: It causes the content to render at different vertical positions than every other page, creating a visual inconsistency during transitions. The AppBar should always occupy its standard space.
- The visual effect these pages wanted (content flowing under a transparent AppBar) can be achieved instead by making the AppBar background transparent with no elevation, which `CustomAppBar` already supports — just ensure the gradient starts from the top of the safe area, not from below the AppBar.

**Rule 3 — Background is ALWAYS fixed (never scrolls with content):**
- Standardize all pages to this structure:
  ```
  Scaffold(
    appBar: CustomAppBar(...),
    body: AppBackground(         // <-- Fixed, wraps everything
      child: SafeArea(
        child: [ScrollView/Content],   // <-- Only this scrolls
      ),
    ),
  )
  ```
- The `AppBackground` widget (from `core/app_background.dart`) applies the gradient + background.png decoration. It wraps the `SafeArea` and scroll content, so the background stays pinned while content scrolls over it.
- Currently 8 views have the background Container INSIDE the ScrollView — move it outside.

**Rule 4 — Remove all manual AppBar height padding:**
- `NotificationsView`: remove the `padding: EdgeInsets.only(top: appBarHeight + ...)` from the ListView. Instead, use standard `Scaffold.appBar` which automatically reserves space.
- `AskEqiupeIoView`: replace the `SizedBox(height: kToolbarHeight + 48)` spacer with a proper `CustomAppBar`.

**Rule 5 — Standardize `SizedBox.expand` usage:**
- Remove all `SizedBox.expand > Container(decoration)` wrappers. Replace with the `AppBackground` widget which already handles `width: double.infinity, height: double.infinity`.

### 2.4 CustomAppBar Enhancements

The existing `CustomAppBar` in `appbar/CustomAppBar.dart` needs minor enhancements to support all page types:

**Enhancement 1 — Consistent back button animation:**
- Wrap the back `IconButton` in the new `AnimatedButton` wrapper (from Section 1.2) so pressing back gives immediate scale feedback.

**Enhancement 2 — Smooth team switcher transition:**
- The `_TeamSwitcher` already has `AnimatedContainer` (120ms) — increase to **200ms easeOutCubic** for consistency with other animations.
- Add an `AnimatedRotation` on the dropdown arrow icon that rotates 180 degrees when the menu opens and back when it closes.

**Enhancement 3 — AppBar elevation animation:**
- Add a scroll-aware elevation: when the page content is scrolled to the top, AppBar elevation is 0 (flat). When the user scrolls down, elevation smoothly transitions to 2 over 200ms using `AnimatedContainer`.
- Implement by accepting an optional `ScrollController` in `CustomAppBar` and listening to scroll position. This gives a subtle "lift" effect when content scrolls under the AppBar.

**Enhancement 4 — Consistent notification badge:**
- The notification icon in `CustomAppBar` should have an animated badge (small red dot) when there are unread notifications. Use `AnimatedScale` to pop the badge in from 0 to 1 over 200ms easeOutBack when it first appears.

### 2.5 Files to Modify

| File | Change |
|---|---|
| `MatchDetailView.dart` | Remove `extendBodyBehindAppBar: true`, restructure body |
| `GameDetailHistoryView.dart` | Remove `extendBodyBehindAppBar: true`, restructure body |
| `NotificationsView.dart` | Remove manual AppBar height padding, use standard Scaffold.appBar |
| `AskEqiupeIoView.dart` | Replace SizedBox spacer with CustomAppBar |
| `CustomAppBar.dart` | Add back button animation, smoother team switcher, scroll-aware elevation |
| All 25+ page files | Replace inline `Container(decoration: BoxDecoration(gradient...))` with `AppBackground` widget |
| 8 views with background inside ScrollView | Move `AppBackground` outside the scroll content |

---

## 3. Keyboard Smoothness System

This is the area you specifically asked to focus on. Here's the page-by-page keyboard plan.

### 2.1 LoginView.dart — Keyboard

**Current state:** Uses `resizeToAvoidBottomInset: false` + `AnimatedPadding` (180ms easeOutCubic). Two TextFields (email, password) with no explicit `FocusNode`. Keyboard appears and the content shifts via animated padding, but there's no scroll-to-focused-field behavior.

**Problems:**
1. When the password field is focused and the keyboard appears, the field might be partially hidden on shorter devices because the `SingleChildScrollView` doesn't auto-scroll to it.
2. No `FocusNode` management means no programmatic control over focus flow.
3. The `TextInputAction.next` on the email field triggers the next field, but there's no smooth scroll to accompany it.

**Optimization:**
- Add two `FocusNode` instances (`_emailFocus`, `_passwordFocus`) — properly disposed.
- On each `FocusNode.addListener`, call `Scrollable.ensureVisible(context, duration: Duration(milliseconds: 300), curve: Curves.easeOutCubic)` to smoothly scroll the focused field into view.
- Replace the `AnimatedPadding` with the new `SmoothKeyboardMixin` padding (200ms easeOutCubic, consistent with the rest of the app).
- Add a `GestureDetector(onTap: () => FocusManager.instance.primaryFocus?.unfocus())` on the outer `Scaffold` body to dismiss keyboard on background tap.

### 2.2 SignUpView.dart — Keyboard

**Current state:** Uses `resizeToAvoidBottomInset: false` + `AnimatedPadding` (120ms easeOut). Seven TextFields via `_tf()` helper. The DOB field calls `_pickDob()` which manually unfocuses, waits 80ms, then shows the date picker.

**Problems:**
1. Seven fields with no `FocusNode` management — tabbing through fields with `TextInputAction.next` works, but there's no scroll-to-field animation.
2. The 120ms padding animation is too fast — it looks like a jump, not a glide.
3. The 80ms delay before showing the date picker is too short on slower devices and too long on fast ones — it should be driven by the unfocus animation completing, not a fixed timer.
4. When moving from the Phone field to the DOB field (which opens a picker), the keyboard dismissal and picker appearance clash.

**Optimization:**
- Add seven `FocusNode` instances (one per field) — all properly disposed.
- Each `FocusNode.addListener` calls `ensureVisible` with 300ms easeOutCubic.
- Replace `AnimatedPadding` with `SmoothKeyboardMixin` (200ms easeOutCubic).
- For DOB field: instead of `Future.delayed(80ms)`, use `FocusManager.instance.primaryFocus?.unfocus()` followed by `WidgetsBinding.instance.addPostFrameCallback((_) => showDatePicker(...))` — this waits for the frame to render the unfocused state before showing the picker, which is more reliable than a fixed delay.
- Add background-tap-to-dismiss as in LoginView.

### 2.3 CompleteProfileView.dart — Keyboard

**Current state:** Same pattern as SignUpView. But has a **critical bug** at line 347: a `TextEditingController(text: widget.googleAuth.user!.email)` is created inline in the `build()` method — this creates a new controller on every rebuild, leaking memory.

**Optimization:**
- Fix the bug: move the email `TextEditingController` to `initState` and dispose in `dispose()`.
- Add `FocusNode` for the editable name field.
- Apply `SmoothKeyboardMixin`.
- Same DOB picker fix as SignUpView.

### 2.4 AddTeamView.dart — Keyboard

**Current state:** One `TextFormField` (team name) + two `AnimatedDropdown` wrappers. Uses `resizeToAvoidBottomInset: false` with manual keyboard padding.

**Optimization:**
- Add `FocusNode` for the team name field.
- Apply `SmoothKeyboardMixin`.
- When the dropdown is tapped, unfocus the text field first (prevent keyboard + dropdown overlay).

### 2.5 AddClubView.dart — Keyboard

**Current state:** Two `TextFormField`s (club name, location) + one date picker field. No `TextEditingController` used — fields use `onChanged` directly.

**Optimization:**
- Add `FocusNode` for both text fields.
- Apply `SmoothKeyboardMixin`.
- Same date picker fix as SignUpView (PostFrameCallback instead of delay).

### 2.6 AddEventView.dart — Keyboard

**Current state:** One `TextEditingController` for description (4-line multiline). Date/time fields are `readOnly`. Uses `resizeToAvoidBottomInset: false`.

**Problems:** The description field is multiline (4 lines) and is positioned near the bottom of the form. When focused, the keyboard likely covers it on shorter devices.

**Optimization:**
- Add `FocusNode` for the description field.
- Apply `SmoothKeyboardMixin` — the `ensureVisible` call will smoothly scroll the description field above the keyboard.
- For date/time pickers: apply the same PostFrameCallback pattern.

### 2.7 AddMembersView.dart — Keyboard

**Current state:** Three `TextEditingController`s (email, jersey, position). Uses `resizeToAvoidBottomInset: false`.

**Optimization:**
- Add three `FocusNode` instances.
- Apply `SmoothKeyboardMixin`.
- Jersey number field uses `TextInputType.number` — ensure the keyboard type transitions smoothly (no flash) by pre-setting the `FocusNode` before focus.

### 2.8 AddAnnouncementView.dart — Keyboard

**Current state:** One `TextEditingController` for caption. `onChanged: (_) => setState(() {})` triggers a full rebuild on every keystroke.

**Problems:** The `setState(() {})` on every keystroke rebuilds the entire widget tree — including the image picker, ChoiceChips, and submit button — just to recalculate a simple `_isValid` boolean.

**Optimization:**
- Replace `onChanged: (_) => setState(() {})` with a `ValueNotifier<bool>` for validity. Use `ValueListenableBuilder` only around the submit button so only the button rebuilds when text changes.
- Add `FocusNode` for the caption field.
- Apply `SmoothKeyboardMixin`.

### 2.9 ChatView.dart — Keyboard

**Current state:** This is the most complex keyboard scenario. Uses `WidgetsBindingObserver.didChangeMetrics()` to track keyboard height. Has emoji picker that mimics keyboard height. `FocusNode` is used (good). Toggle between keyboard and emoji picker.

**Problems:**
1. The emoji toggle uses `Future.delayed(Duration(milliseconds: 80))` before showing the emoji picker — same fixed-delay problem.
2. When switching from emoji picker to keyboard, there's a visible "gap" flicker as the keyboard animates up.
3. `_scrollToBottom()` uses `animateTo(0)` with 200ms — this can be jarring if the user sends a long message.

**Optimization:**
- For emoji/keyboard toggle: use `ValueNotifier<double>` for keyboard height and drive the emoji picker height from it with a `AnimatedContainer` of 250ms easeOutCubic — this prevents the gap flicker.
- Replace the 80ms `Future.delayed` with a `WidgetsBinding.addPostFrameCallback`.
- Change `_scrollToBottom` from 200ms to **300ms with easeOutCubic** — smoother landing.
- Add a `SlideTransition` on new messages appearing in the list: each new message slides up from `Offset(0, 0.3)` with a 250ms `easeOutCubic` fade+slide. This makes the chat feel alive.

### 2.10 SearchView.dart — Keyboard

**Current state:** Uses `resizeToAvoidBottomInset: false`. Search TextField dispatches `UpdateQuery` to BLoC on every keystroke with no debounce.

**Problems:**
1. No debounce — every keystroke triggers a state emission and a full rebuild of the result list.
2. The result list (members, files, plans) rebuilds instantly on each keystroke, which can cause frame drops.

**Optimization:**
- Add a **300ms debounce** on the `onChanged` callback (use a `Timer` that resets on each keystroke).
- Apply `SmoothKeyboardMixin`.
- Add `FocusNode` and auto-focus on page open for immediate typing.

### 2.11 AskEqiupeIoView.dart — Keyboard

**Current state:** `TextEditingController` and `ScrollController` are **not disposed** — memory leak. Uses `jumpTo(maxScrollExtent)` after a 100ms `Future.delayed` which is unreliable.

**Optimization:**
- Fix: dispose `_controller` and `_scrollController` in `dispose()`.
- Replace `jumpTo` + 100ms delay with `WidgetsBinding.addPostFrameCallback((_) => _scrollController.animateTo(_scrollController.position.maxScrollExtent, duration: Duration(milliseconds: 300), curve: Curves.easeOutCubic))`.
- Apply `SmoothKeyboardMixin`.
- Check `mounted` before the second `setState` in the fake AI response callback.

### 2.12 Dialog TextFields (MemberDetailView, PlayerProfileView, TeamStatsView, PlansView)

**Current state:** Multiple views show dialogs with TextFields. These dialogs use `showDialog` which creates a new route, so keyboard handling is separate. But the dialog content often overflows when the keyboard appears.

**Optimization:**
- Wrap all dialog content in `SingleChildScrollView` (some already do, verify all).
- Use `autofocus: true` on the first field of each dialog (some already do, make consistent).
- Add `TextInputAction.next` / `.done` chaining across dialog fields.

---

## 4. Page Transition Animation System

### 4.1 Current State Analysis

| Route Type | Duration | Effect | Used By |
|---|---|---|---|
| `AppPageRoute` | 320ms enter / 280ms reverse | Slide+Fade | ~30 navigation calls |
| `AppFadeRoute` | 450ms enter / 300ms reverse | Fade only | Splash, Onboarding, Logout |
| `MaterialPageRoute` | 300ms | Default material slide | 3 calls in Members module |

**Problems:**
1. Three different transition styles with no cohesion.
2. `MaterialPageRoute` in `PlayerProfileView` and `MedicalRecordDetailView` uses the default Android slide, which looks different from the rest of the app.
3. No hero animations between list items and detail pages.
4. Bottom sheets (`showModalBottomSheet` in EventView) appear with the default slide-up, which feels disconnected.

### 4.2 Transition Standardization

**Replace all `MaterialPageRoute` with `AppPageRoute`:**
- `PlayerProfileView.dart` line navigating to `AddMedicalRecordView` — change to `AppPageRoute`.
- `PlayerProfileView.dart` line navigating to `MedicalRecordDetailView` — change to `AppPageRoute`.
- `MedicalRecordView.dart` line navigating to `MedicalRecordDetailView` — change to `AppPageRoute`.

### 4.3 Bottom Sheet Transition Enhancement

**What:** Create `AppBottomSheet` helper in `core/app_transitions.dart`.
**Why:** The `showModalBottomSheet` in `EventView.dart` uses default Material animation. A custom transition feels more integrated.

**Details:**
- Use `showModalBottomSheet` with `transitionAnimationController` parameter set to a custom `AnimationController` with **350ms duration** and `Curves.easeOutExpo` curve.
- Add a subtle `ScaleTransition` on the sheet from `0.95` to `1.0` alongside the default slide-up.
- Apply to `EventView` bottom sheet and `ChatView` attachment menu bottom sheet.

### 4.4 Hero Transitions for List-to-Detail

**What:** Add `Hero` widgets between list items and their detail pages.
**Why:** When tapping a member in the team grid, the avatar should smoothly fly to the profile page. When tapping a chat conversation, the avatar should fly to the chat header. This creates visual continuity.

**Details (no style changes — just wrapping existing widgets):**
- **TeamView member grid -> PlayerProfileView/ProfileView:** Wrap member `CircleAvatar` in `Hero(tag: 'member-${member.userId}')`.
- **MessagesView conversation -> ChatView:** Wrap conversation `CircleAvatar` in `Hero(tag: 'chat-${conversation.id}')`.
- **HomeView event card -> DayEventsDetailView:** Wrap the event type icon in `Hero(tag: 'event-${event.id}')`.
- **PlansView plan card -> AddPlansView (edit mode):** Wrap the plan category icon in `Hero(tag: 'plan-${plan.id}')`.

### 4.5 Tab Transitions in TeamView

**Current state:** `TeamView` uses a `TabController` with `TabBarView` for Members/Stats/Plans. The default swipe transition is fine, but there's no entrance animation when the tabs first load.

**Optimization:**
- Add a `FadeTransition` on the `TabBarView` that fades from 0 to 1 over 300ms when the page first mounts. This prevents the "content pop-in" when TeamView loads.

### 4.6 MainNavigation Page Switching

**Current state:** `_AnimatedPageStack` uses `PageView.animateToPage` with 400ms easeInOutCubic. This is good, but the bottom nav icons have no transition feedback beyond `AnimatedSlide`.

**Optimization:**
- Keep the 400ms page animation — it's smooth.
- Add a subtle `AnimatedScale` (1.0 -> 1.15 -> 1.0 over 250ms easeOutBack) on the bottom nav icon when it becomes selected — a tiny "pop" that signals the tap was registered.
- The existing `AnimatedSlide` (320ms easeOutBack) on the icon is good — keep it.

---

## 5. Universal Button Animation System

Every button in the app gets consistent press feedback. Here is the exact inventory and plan.

### 5.1 Primary Action Buttons (ElevatedButton / FilledButton)

These are the big action buttons: "Log in", "Sign up", "Complete profile", "Add Team", "Create Club", "Send Invitation", "Add plan", "Save", etc.

**Count:** 38 instances across all pages.

**Animation:** Wrap in `AnimatedButton` with:
- Scale: `1.0 -> 0.95` on press, `0.95 -> 1.0` on release.
- Duration: 80ms down, 160ms up with `Curves.easeOutBack`.
- No color change (preserves existing style).

**Pages with primary buttons:**
- LoginView: "Log in" button, Google auth button
- SignUpView: "Finish sign up" button, Google auth button
- CompleteProfileView: "Complete profile" button
- OnboardingView (main.dart): "Next" buttons (3), "Create Account" button
- CongratsView: "Let's start" button
- HomeView: "Add team/club" button, announcement Save/Delete buttons
- AddTeamView: "Add Team" button, "Allow Location" button
- AddClubView: "Create Club" button
- AddEventView: submit button
- AddMembersView: "Send Invitation" button
- AddAnnouncementView: "Add announcement" button
- AddPlansView: "Add plan" / "Save plan" button
- AddMedicalRecordView: "Save" button
- ProfileView: "Save" / "Cancel" buttons
- GameSquadView: "Save Squad" button
- MemberDetailView: "Record" button, "Add Fitness Record" button
- ChatView: send button (already an IconButton, use subtler scale)
- SettingsView: "Log out" button
- AttendanceView: ChoiceChips (Present/Absent/Late/Excused)

### 5.2 Secondary Buttons (OutlinedButton / TextButton)

These are supplementary actions: "Create Account" (login), "Cancel", "Decline", "Back", "Skip", etc.

**Count:** 26 instances across all pages.

**Animation:** Wrap in `AnimatedButton` with:
- Scale: `1.0 -> 0.96` on press, `0.96 -> 1.0` on release.
- Duration: 60ms down, 140ms up with `Curves.easeOutBack`.

**Pages with secondary buttons:**
- LoginView: "Create Account", "Forgot password?"
- OnboardingView: "Back", "Skip" links
- JoinTeamView: "Decline" / "Accept" per invitation
- DayEventsDetailView: "Take attendance", "Edit", "Delete" per event
- EventView: "Add event", "Take attendance", "Edit", "Delete"
- IncomingRequestsView: "Cancel" per invitation, "Retry"
- Dialog Cancel/Close buttons (all dialogs across the app)

### 5.3 Icon Buttons

These are the small circular tap targets: back arrows, notifications bell, search icon, edit pencil, close X, send arrow, mic, emoji, attach.

**Count:** 45+ instances.

**Animation:** Wrap in `AnimatedButton` with:
- Scale: `1.0 -> 0.85` on press, `0.85 -> 1.0` on release.
- Duration: 50ms down, 120ms up with `Curves.easeOutCubic`.
- Add a subtle `AnimatedOpacity` from `1.0 -> 0.6` on press.

**Key locations:**
- CustomAppBar: back, search, notifications icons
- MainNavigation: bottom nav icons (5)
- ChatView: send, mic, emoji, attach, cancel recording icons
- ProfileView: edit icon
- MedicalRecordView: clearance toggle, edit icon per record
- All dialog close (X) buttons

### 5.4 Card Taps (GestureDetector / InkWell)

These are tappable cards: member cards, message cards, notification cards, plan cards, event list items.

**Current state:** Only ~8 cards in HomeView/DayEventsDetailView use `AnimatedPressable`.

**Plan:** Wrap ALL tappable cards in `AnimatedPressable` (already exists in `app_transitions.dart`):

| View | Card Type | Currently Animated? |
|---|---|---|
| HomeView event carousel | Day event cards | Yes (AnimatedPressable) |
| HomeView announcements | Announcement cards | Yes (AnimatedPressable) |
| DayEventsDetailView | Event detail cards | Yes (AnimatedPressable) |
| TeamView | Member grid cards | **No** |
| MessagesView | Conversation cards | **No** |
| NotificationsView | Notification cards | **No** |
| PlansView | Plan cards | **No** (has swipe slider but no press scale) |
| SearchView | Member/File/Plan items | **No** |
| PlayerSelectionView | Player list items | **No** |
| JoinTeamView | Invitation cards | **No** |
| IncomingRequestsView | Request cards | **No** |
| SettingsView | Team cards, setting items | **No** |
| EventView | Bottom sheet event items | **No** |
| GameSquadView | Player tiles | **No** |
| MedicalRecordView | Record cards | **No** |

**For each card above:** Wrap the outermost `GestureDetector` or `InkWell` with `AnimatedPressable(onTap: ..., child: existingCard)`. This adds the scale-down-on-press animation with zero style changes.

### 5.5 FAB (Floating Action Button) in MainNavigation

**Current state:** The FAB toggles `_fabExpanded` and shows action buttons with `AnimatedPressable`. The action buttons also have `AnimatedContainer` for hover.

**Optimization:**
- Add a `RotationTransition` on the FAB icon: when `_fabExpanded` toggles, rotate the icon 45 degrees (plus to X) over 250ms with `easeOutBack`. This is a standard FAB pattern and signals the state change.
- Stagger the action buttons: when expanding, each action button appears with a 40ms delay from the bottom up, using `SlideTransition` from `Offset(0, 0.5)` + `FadeTransition`. When collapsing, they disappear simultaneously with a 150ms fade.

---

## 6. Page-by-Page Optimization

### 6.1 Splash Screen (main.dart — SplashView)

**Current state:** 5-second repeating rotation on the logo. Navigates to onboarding/main after 4 seconds.

**Optimization:**
- Change rotation from `repeat()` to a single `forward()` — continuous spinning looks like loading, not branding.
- Instead: fade in the logo from 0 opacity over 600ms, then scale from 0.8 to 1.0 over 800ms with `easeOutBack` (bounce), then hold for 2 seconds, then fade out over 400ms before navigating.
- The "EQUIPEX" text should fade in 400ms after the logo animation starts (staggered).

### 6.2 Onboarding (main.dart — OnboardingView + OnboardingPage)

**Current state:** Already has good staggered animations (image fade+scale, title slide, description slide, button fade) with a 620ms controller. `AnimatedContainer` on page dots.

**Optimization (minor polish):**
- The `_imageScale` uses `easeOutBack` which is great — keep it.
- Add a `ParallaxEffect` to the background image: as pages swipe, the background image shifts slightly (10% of the swipe distance) in the opposite direction. Implement by listening to the `PageView`'s `PageController` scroll position and applying a `Transform.translate` on the background container.
- Page dots: increase the `AnimatedContainer` duration from 300ms to 350ms and add a color transition (current dot green, others grey) — currently only width changes, which is good.

### 6.3 HomeView

**Current state:** Complex page with event carousel (`PageView.builder`), announcements with swipe sliders, pull-to-refresh, and add buttons. Has `AnimatedPressable` on cards. `_EditableAnnouncementCard` has a FadeTransition+SizeTransition for edit mode (320ms).

**Problems:**
1. `_groupByDay` in `_EventCarousel.build` creates a new map and sorts on every build — unnecessary recomputation.
2. No entrance animation when the page first loads — content just appears.

**Optimization:**
- **Memoize `_groupByDay`:** Compute it in a `didUpdateWidget` check and store as state, only recomputing when the event list changes.
- **Staggered entrance animation:** When HomeView first loads (or returns from background), animate each section in sequence:
  1. Event carousel slides up from `Offset(0, 0.1)` + fades in over 300ms.
  2. Announcements section slides up from `Offset(0, 0.1)` + fades in over 300ms, starting 100ms after the carousel.
  - Use a `AnimationController` in the parent and `Interval`-based `CurvedAnimation` for each section.
- **Event carousel page indicator:** Already uses `AnimatedContainer` (250ms) — increase to 300ms for smoother dot sliding.

### 6.4 EventView

**Current state:** Calendar view with bottom sheet for day events. Uses `TableCalendar`. No explicit animations.

**Optimization:**
- **Calendar day tap:** Add a subtle ink ripple (already built into `TableCalendar`, ensure `calendarStyle.markerDecoration` doesn't suppress it).
- **Bottom sheet:** Apply the enhanced `AppBottomSheet` transition (350ms easeOutExpo + subtle scale from 0.95).
- **Event list items in bottom sheet:** Stagger them with 50ms delays, each sliding up from `Offset(0, 0.15)` + fading in over 200ms.

### 6.5 TeamView

**Current state:** `SliverGrid` of member cards in `CustomScrollView`. Tab switching via `TabBarView`. No card animations.

**Optimization:**
- **Member cards:** Wrap each in `AnimatedPressable`.
- **Grid entrance:** When the members tab is first shown, stagger the grid items: each card fades in with a 30ms delay from the previous one, scaling from 0.95 to 1.0 over 250ms. Use `SliverAnimatedGrid` or manual stagger with index-based delays.
- **Message button on member card:** Apply `AnimatedButton` wrapper.
- **Tab switching:** Add a crossfade between tab contents (TabBarView already handles this via swipe, but the initial load should have a subtle fade-in).

### 6.6 ProfileView / PlayerProfileView

**Current state:** Long scrollable profile with multiple sections. No animations. `PlayerProfileView` is the largest file at ~2500 lines.

**Optimization:**
- **Section entrance:** As the user scrolls down, each section (profile card, info card, medical banner, biometrics, fitness, medical records, stats, videos) should fade in when it enters the viewport. Implement with `VisibilityDetector` (or a custom `SliverAnimatedOpacity` approach).
  - Each section: fade from 0 to 1 + slide from `Offset(0, 0.05)` to zero over 400ms easeOutCubic.
  - Only trigger once (not on every scroll direction change).
- **Profile card avatar:** Add `Hero` tag for the member avatar (connects to TeamView grid).
- **Expandable dropdowns (biometrics, fitness):** Already use `AnimatedDropdown` — keep this.

### 6.7 ChatView

**Current state:** Most animated view in the app. Has swipe-to-reply, recording bar animations, emoji toggle. `ListView.builder` with `reverse: true`.

**Optimization:**
- **New message animation:** When a message is sent or received, it should slide in from `Offset(0, 0.3)` + fade in over 250ms easeOutCubic. Implement by wrapping each message bubble in an `AnimatedSwitcher` or by tracking "just added" messages and applying a one-time animation.
- **Send button:** Add a rotation animation — when the text field has content, the mic icon rotates 90 degrees and crossfades to the send icon over 200ms. Currently this is a hard swap.
- **Typing indicator:** When the input bar text changes height (multiline), animate the height change over 150ms easeOutCubic (use `AnimatedContainer` or `AnimatedSize` on the input bar).
- **Scroll-to-bottom button:** Add a `FloatingActionButton`-style button that appears (fade+slide from bottom) when the user scrolls up more than 200px. Tapping it calls `_scrollToBottom` with the improved 300ms easeOutCubic animation.

### 6.8 MessagesView

**Current state:** Simple `ListView.builder` of conversation cards with no animations.

**Optimization:**
- **Card press:** Wrap each `_MessageCard` in `AnimatedPressable`.
- **List entrance:** Stagger conversation cards with 40ms delays, each fading in from `Offset(0, 0.08)` over 200ms.
- **Unread badge:** Add a subtle `ScaleTransition` pulse on unread count badges (scale 1.0 -> 1.1 -> 1.0 over 1.5s, repeating) to draw attention.

### 6.9 NotificationsView

**Current state:** `ListView.builder` with `AnimatedContainer` (200ms) for read/unread color change. Dispatches `MarkAllRead` in `build` via `addPostFrameCallback`.

**Optimization:**
- **Card press:** Wrap each `_NotificationCard` in `AnimatedPressable`.
- **Expand animation:** When a notification card expands (toggles `_expanded`), use `AnimatedCrossFade` or `AnimatedSize` with 250ms easeOutCubic instead of the current instant show/hide.
- **List entrance:** Same staggered entrance as MessagesView.

### 6.10 PlansView

**Current state:** `_PlanActionSlider` has a 180ms easeOutCubic slide animation. `_DocumentChip` shows download progress. No card entrance animations.

**Problems:** `IntrinsicHeight` in `_PlanCard` triggers expensive two-pass layout on every card.

**Optimization:**
- **Remove `IntrinsicHeight`:** Replace with a fixed-minimum-height `ConstrainedBox` or use `CrossAxisAlignment.stretch` in the Row — this eliminates the two-pass layout cost.
- **Card entrance:** Staggered fade+slide like other list views.
- **Document chip:** Add a subtle loading shimmer effect when downloading (a gradient animation across the chip surface).

### 6.11 AddPlansView (Tactical Board)

**Current state:** The largest file (2191 lines). Has `AnimatedScale` and `AnimatedPositioned` on player markers. `_CourtPainter.shouldRepaint` always returns `true`. Presets stored in `SharedPreferences`.

**Problems:**
1. `shouldRepaint` returning `true` means the court repaints 60 times per second even when idle — massive waste.
2. Preset list `_TacticalPresets.all()` recreates objects on every build.
3. ~660 lines of commented-out code in related files.

**Optimization:**
- **Fix `shouldRepaint`:** Compare `arrows`, `pendingPoints`, `players`, and `showGrid` between old and new painter. Only repaint when data actually changes.
- **Memoize presets:** Store `_TacticalPresets.all()` result in a `late final` field, not in build.
- **Drawing animation:** When a user finishes drawing an arrow (lifts finger), animate the arrow's opacity from 0.5 to 1.0 over 200ms — a subtle "ink setting" effect.
- **Player snap animation:** The existing `AnimatedPositioned` (320ms easeOutCubic) for snap-back is good — keep it.

### 6.12 SettingsView

**Current state:** `ListView` of settings items. No animations beyond `AnimatedDropdown` for language.

**Optimization:**
- **Setting items:** Wrap each `GestureDetector` (team cards, log out button) in `AnimatedPressable`.
- **Theme toggle:** Add a `AnimatedSwitcher` with a crossfade (200ms) when switching between light/dark mode icons.
- **Team switch:** When a team card is selected, add a `AnimatedContainer` (200ms) border color transition from grey to green — currently this might already happen if using `BlocBuilder`, but make it explicit.

### 6.13 MatchDetailView

**Current state:** Read-only detail view. `FutureBuilder` creates a new Future on every rebuild.

**Problems:** `StatsService()` is instantiated inline every build, and the `FutureBuilder` re-fires the API call on every parent rebuild.

**Optimization:**
- **Cache the future:** Create `_statsFuture` in `initState` (convert to `StatefulWidget`) and pass it to `FutureBuilder`. This prevents redundant API calls.
- **Section entrance:** Staggered fade-in for match card, stats card, and squad section.

### 6.14 GameSquadView

**Current state:** `SingleChildScrollView` with all players built eagerly. No animations.

**Problems:** Uses spread operator `...players.map()` inside a Column — all players built at once.

**Optimization:**
- **Replace Column with `ListView.builder`** for the player list — lazy loading for large rosters.
- **Checkbox animation:** The `Checkbox` widget already has a built-in animation. Add `AnimatedPressable` on the player tile for tap feedback.
- **Save button:** Apply `AnimatedButton` wrapper.

### 6.15 AttendanceView

**Current state:** `ListView` with spread `...members.map()` — builds all items eagerly.

**Optimization:**
- **Replace spread with `ListView.builder`** — lazy item building.
- **ChoiceChip selection:** Add a subtle `AnimatedScale` pulse (1.0 -> 1.05 -> 1.0 over 200ms) when a chip is selected.

### 6.16 Upload Views (UploadPdfView, UploadVideoView)

**Current state:** No ScrollView — content can overflow on small screens. No animations.

**Optimization:**
- **Wrap content in `SingleChildScrollView`** to prevent overflow.
- **Upload button:** Apply `AnimatedButton` wrapper.
- **Upload progress:** When upload starts, add an animated progress indicator (linear with indeterminate animation).

### 6.17 IncomingRequestsView

**Current state:** `ListView.builder` with `RefreshIndicator`. No animations. Doesn't use BLoC.

**Optimization:**
- **Card press:** Wrap invitation cards in `AnimatedPressable`.
- **Cancel button:** Apply `AnimatedButton`.
- **Dismiss animation:** When an invitation is cancelled, animate it out with `AnimatedList.removeItem` using a `SizeTransition` + `FadeTransition` over 300ms — the item shrinks and fades rather than just disappearing.

---

## 7. BLoC Layer Optimizations

These changes improve performance without any visual impact — they reduce unnecessary rebuilds, prevent frame drops, and ensure state transitions are crisp.

### 7.1 Add Debouncing to Search

**File:** `search/search_bloc.dart`
**Why:** `UpdateQuery` fires on every keystroke with no debounce. The computed getters `filteredMembers`, `filteredFiles`, `filteredPlans` run on every access — with large member lists and multiple PDF field checks, this causes frame drops during fast typing.

**Fix:**
- Add `transformer: restartable()` to the `UpdateQuery` event handler (using `bloc_concurrency` package) with a 300ms debounce.
- Pre-compute filtered results in the handler and store them in state instead of using getters.

### 7.2 Add Debouncing to Messages Search

**File:** `messages/messages_bloc.dart`
**Why:** `SearchMembers` also fires on every keystroke.

**Fix:** Same `restartable()` transformer with 300ms debounce.

### 7.3 Fix `add()` Chaining Pattern

**Files:** `attendance_bloc.dart`, `medical_bloc.dart`, `team_bloc.dart`
**Why:** These BLoCs dispatch events from inside handlers (`add(LoadAttendance(...))` after `RecordAttendance`). This creates a visible loading flicker (isLoading true -> data loads -> isLoading true again -> data loads again).

**Fix:** Extract the load logic into a private method and call it directly instead of dispatching a new event:
```dart
// Before (causes double loading)
emit(state.copyWith(isLoading: true));
await _service.record(...);
add(LoadAttendance(...));  // triggers another isLoading: true

// After (single smooth load)
emit(state.copyWith(isLoading: true));
await _service.record(...);
await _loadData(emit);  // directly calls the load logic
```

### 7.4 Fix Silent Error Swallowing

**Files:** `plans_bloc.dart` (attachment uploads), `chat_bloc.dart` (toggle reaction)
**Why:** Failed attachment uploads are silently caught with `catch (_) {}`. The user gets no feedback.

**Fix:** Accumulate failed filenames and emit them in the state's error field after all uploads complete.

### 7.5 Cache Computed Properties

**File:** `event_bloc.dart`
**Why:** `_rebuildEventsByDay` is called on every mutation. `upcomingEvents` getter re-filters and re-sorts on every access.

**Fix:** Compute `upcomingEvents` once in `_rebuildEventsByDay` and store it in state alongside `eventsByDay`.

### 7.6 Remove UI Imports from BLoC

**File:** `search_bloc.dart`
**Why:** Imports `package:flutter/material.dart` for `Icons` and `Colors` — mixing UI concerns into the BLoC layer.

**Fix:** Move icon/color mappings to the view layer.

---

## 8. Scroll & List Performance

### 8.1 Replace Eager Lists with Lazy Builders

| View | Current | Replace With |
|---|---|---|
| AttendanceView | `...members.map()` in ListView | `ListView.builder(itemCount: members.length)` |
| GameSquadView | `...players.map()` in Column | `ListView.builder(itemCount: players.length)` |
| TeamStatsView (stats table) | `...tableRows.map()` in Column | `ListView.builder` for tables > 20 rows |
| TeamStatsView (game history) | `...gameHistory.map()` in Column | `ListView.builder` for history > 20 items |
| DayEventsDetailView | `...eventsForDay.map()` in Column | Keep as-is (typically < 10 events/day) |

### 8.2 Add `const` Constructors

**Why:** Widgets with `const` constructors don't rebuild when their parent rebuilds. Many simple widgets in the app are missing `const`.

**Targets:**
- All `SizedBox` spacers without variable parameters → `const SizedBox(height: 16)`
- All `Text` widgets with static strings → `const Text('...')`
- All `EdgeInsets` values → `const EdgeInsets.all(16)`
- `Divider()` instances → `const Divider()`

### 8.3 RepaintBoundary on Expensive Widgets

**What:** Wrap expensive painting widgets in `RepaintBoundary` to isolate their repaint layer.
**Where:**
- `_BasketballTacticalBoard` in `AddPlansView.dart` — this is a CustomPaint that (currently) repaints every frame.
- `LineChart` in `TeamStatsView.dart` — chart repaints are expensive.
- Each `_MessageBubble` in `ChatView.dart` — prevents one bubble repaint from dirtying the entire list.

---

## 9. Implementation Priority & Order

### Phase 1 — Foundation (Do First)

| # | Task | Impact | Effort |
|---|---|---|---|
| 1 | Standardize all AppBars to use `CustomAppBar` consistently (Section 2) | High — eliminates visual jumps between pages | Medium |
| 2 | Remove `extendBodyBehindAppBar` from MatchDetailView & GameDetailHistoryView | High — fixes content position inconsistency | Low |
| 3 | Extract `AppBackground` as the single background wrapper for all pages (fixed, never scrolling) | High — consistent background, eliminates 25+ duplicated decorations | Medium |
| 4 | Create `SmoothKeyboardMixin` | High — fixes keyboard on all 14 pages | Medium |
| 5 | Create `AnimatedButton` wrapper | High — adds feedback to 100+ buttons | Medium |
| 6 | Upgrade `AppPageRoute` / `AppFadeRoute` | High — every navigation feels smoother | Low |
| 7 | Fix `_CourtPainter.shouldRepaint` | High — stops wasting 60fps of GPU | Trivial |
| 8 | Fix `CompleteProfileView` controller bug | Critical — memory leak | Trivial |
| 9 | Fix `AskEqiupeIoView` controller disposal | Critical — memory leak | Trivial |
| 10 | Fix `MatchDetailView` FutureBuilder | High — stops redundant API calls | Low |
| 11 | Add scroll-aware elevation to `CustomAppBar` | Medium — subtle polish | Low |

### Phase 2 — Button & Card Animations (Do Second)

| # | Task | Impact | Effort |
|---|---|---|---|
| 12 | Wrap all `ElevatedButton`/`FilledButton` in `AnimatedButton` | High — 38 buttons | Medium |
| 13 | Wrap all `OutlinedButton`/`TextButton` in `AnimatedButton` | Medium — 26 buttons | Medium |
| 14 | Wrap all `IconButton` in `AnimatedButton` | Medium — 45+ icons | Medium |
| 15 | Wrap all tappable cards in `AnimatedPressable` | High — 15 card types | Medium |
| 16 | FAB rotation + staggered action buttons | Medium — polish | Low |

### Phase 3 — Keyboard Polish (Do Third)

| # | Task | Impact | Effort |
|---|---|---|---|
| 17 | Apply `SmoothKeyboardMixin` to all 14 form pages | High — consistent keyboard | Medium |
| 18 | Add `FocusNode` management to all form pages | Medium — enables scroll-to-field | Medium |
| 19 | Fix date picker delay pattern (PostFrameCallback) | Medium — removes timing bugs | Low |
| 20 | Add search debouncing (SearchBloc + MessagesBloc) | High — prevents frame drops | Low |
| 21 | Fix `AddAnnouncementView` rebuild-per-keystroke | Medium — less wasteful | Low |

### Phase 4 — Page Transitions & Heroes (Do Fourth)

| # | Task | Impact | Effort |
|---|---|---|---|
| 22 | Replace 3 `MaterialPageRoute` with `AppPageRoute` | Medium — consistency | Trivial |
| 23 | Add Hero animations (member avatar, chat avatar, event icon) | High — visual continuity | Low |
| 24 | Enhanced bottom sheet transitions | Medium — polish | Low |
| 25 | Staggered list entrances (MessagesView, NotificationsView, TeamView grid, etc.) | High — pages feel alive | Medium |
| 26 | HomeView section entrance stagger | Medium — first impression | Low |

### Phase 5 — BLoC & Performance (Do Fifth)

| # | Task | Impact | Effort |
|---|---|---|---|
| 27 | Fix `add()` chaining in 3 BLoCs | Medium — removes loading flicker | Low |
| 28 | Memoize `_groupByDay` in HomeView | Low — minor perf | Trivial |
| 29 | Replace eager lists with `ListView.builder` (4 views) | Medium — helps large data | Low |
| 30 | Add `RepaintBoundary` to 3 expensive widgets | Medium — isolates repaints | Trivial |
| 31 | Add `const` constructors throughout | Low — incremental perf | Medium |
| 32 | Remove 660 lines of dead code from GameDetailHistoryView | Low — code cleanliness | Trivial |
| 33 | Fix silent error swallowing in PlansBloc/ChatBloc | Low — UX correctness | Low |

---

## Summary

**Total changes:** 33 optimization tasks across 5 phases.
**Files touched:** ~60 of the 104 Dart files.
**New files created:** 2 (`smooth_keyboard_mixin.dart`, `animated_button.dart`).
**Visual appearance changes:** Zero. Every animation is additive — existing colors, sizes, fonts, layouts, and spacing are untouched. AppBar and background standardization only fixes inconsistencies so all pages match (no new styles introduced).
**Expected result:** Every tap has physical feedback, every page transition flows with purpose, the keyboard rises and falls like breathing, every AppBar behaves identically, the background stays perfectly still while content glides over it, lists come alive with staggered entrances, and the tactical board stops burning the GPU.
