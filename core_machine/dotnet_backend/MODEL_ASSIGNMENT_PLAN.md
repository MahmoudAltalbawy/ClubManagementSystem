# Equipex Optimization — Claude Model Assignment

## Assignment Philosophy

Each model has a sweet spot. Assigning the wrong model wastes either money (Opus on trivial work) or quality (Haiku on architecture). Here's the logic:

**Opus (8 tasks)** — Gets tasks where a wrong decision breaks multiple files or where the system being created will be imported by 15+ other files. If the task says "create a new reusable system" or "orchestrate animations across views," Opus handles it because it can hold the full codebase context and make trade-offs.

**Sonnet (12 tasks)** — Gets tasks that need good judgment but follow an established pattern. Refactoring 25 files to use AppBackground requires understanding each file's unique layout, but the pattern is clear. Adding Hero animations requires choosing the right tags across pages. Sonnet is fast enough to iterate and smart enough not to break things.

**Haiku (13 tasks)** — Gets mechanical, repetitive tasks where the pattern is 100% defined. "Wrap this button in AnimatedButton" repeated 38 times. "Add dispose() call." "Change MaterialPageRoute to AppPageRoute." Haiku does these 5x faster than Opus at 1/60th the cost with identical results.

---

## Phase 1 — Foundation

### OPUS (3 tasks)

**#4 — Create SmoothKeyboardMixin**
Why Opus: This is a brand-new mixin that 14 different views will import. It needs to handle `WidgetsBindingObserver`, `ValueNotifier<double>` for smooth interpolation, `Scrollable.ensureVisible` integration, and a reusable padding widget. Getting the API wrong means rewriting 14 integration points. Opus's deep reasoning ensures the mixin's interface is right the first time.

**#5 — Create AnimatedButton wrapper**
Why Opus: Another new system imported by 100+ locations. The widget needs to wrap any child button with `GestureDetector` + `ScaleTransition` without interfering with the button's own `onPressed`. It must handle `SingleTickerProviderStateMixin` lifecycle, three different scale profiles (primary/secondary/icon), and edge cases like disabled buttons. Opus understands the animation controller lifecycle and can design a clean API.

**#6 — Upgrade AppPageRoute / AppFadeRoute**
Why Opus: Adjusting animation curves and timing requires taste — Opus can reason about how `easeOutExpo` vs `fastOutSlowIn` *feels* in a page transition and how the incoming slide, outgoing fade, and scale all compose together. Getting the curves wrong makes the whole app feel off. This is a small file change but with app-wide visual impact.

### SONNET (3 tasks)

**#1 — Standardize all AppBars to CustomAppBar**
Why Sonnet: Involves touching 30+ files. Each page has a slightly different layout — some use `extendBodyBehindAppBar`, some have inline back buttons, some use SafeArea differently. Sonnet can assess each file individually and make the right call on how to integrate CustomAppBar without breaking the layout. Not architecturally novel, but requires consistent judgment across many files.

**#3 — Extract AppBackground as single wrapper**
Why Sonnet: Removing duplicated `Container(decoration: BoxDecoration(gradient...))` from 25+ files and replacing with `AppBackground`. Each file wraps the gradient differently (some inside ScrollView, some outside, some with SafeArea inside, some outside). Sonnet can handle the per-file assessment.

**#11 — Add scroll-aware elevation to CustomAppBar**
Why Sonnet: A focused feature addition to one widget. Needs to accept an optional `ScrollController`, listen to scroll position, and drive an `AnimatedContainer` for elevation. Moderate complexity — Sonnet handles it well.

### HAIKU (5 tasks)

**#2 — Remove extendBodyBehindAppBar from 2 views**
Why Haiku: Delete one line from MatchDetailView, one line from GameDetailHistoryView. Trivial.

**#7 — Fix _CourtPainter.shouldRepaint**
Why Haiku: Change `return true` to a comparison of arrows, pendingPoints, players, showGrid. Mechanical — just compare old and new painter fields.

**#8 — Fix CompleteProfileView controller bug**
Why Haiku: Move `TextEditingController(text: ...)` from `build()` to `initState()` and add `dispose()`. Textbook fix.

**#9 — Fix AskEqiupeIoView controller disposal**
Why Haiku: Add two `.dispose()` calls in the `dispose()` method. Check `mounted` before `setState`. Trivial.

**#10 — Fix MatchDetailView FutureBuilder**
Why Haiku: Convert to StatefulWidget, create `_statsFuture` in `initState`, pass to FutureBuilder. Standard pattern.

---

## Phase 2 — Button & Card Animations

### OPUS (1 task)

**#16 — FAB rotation + staggered action buttons**
Why Opus: The FAB in MainNavigation already has complex state (expanded/collapsed, role-based actions, hover effects). Adding a `RotationTransition` on the icon, staggering action button appearances with index-based delays using `Interval`-based `CurvedAnimation`, and coordinating the collapse animation requires understanding the existing animation lifecycle. One wrong controller disposal and the FAB crashes.

### SONNET (1 task)

**#15 — Wrap all tappable cards in AnimatedPressable**
Why Sonnet: 15 different card types across 15 different views. Each card has a different widget tree — some use `GestureDetector`, some use `InkWell`, some are inside `Dismissible` or swipe sliders. Sonnet can assess each card individually and decide how to integrate `AnimatedPressable` without breaking existing gesture detection.

### HAIKU (3 tasks)

**#12 — Wrap all ElevatedButton/FilledButton in AnimatedButton**
Why Haiku: 38 instances. The pattern is identical each time — wrap the existing button with the new `AnimatedButton` widget. Pure find-wrap-save repetition.

**#13 — Wrap all OutlinedButton/TextButton in AnimatedButton**
Why Haiku: 26 instances. Same mechanical pattern as above.

**#14 — Wrap all IconButton in AnimatedButton**
Why Haiku: 45+ instances. Same pattern with the icon-specific scale profile parameter.

---

## Phase 3 — Keyboard Polish

### OPUS (1 task)

**#17 — Apply SmoothKeyboardMixin to 14 form pages**
Why Opus: Although the mixin is already created (task #4), integrating it into 14 pages requires understanding each page's unique layout. ChatView has emoji picker toggling. SignUpView has 7 fields with a date picker. AddPlansView has a tactical board inside a form. Each integration is different and requires reasoning about how the keyboard mixin interacts with existing scroll controllers, animation controllers, and BLoC listeners. Opus ensures no integration breaks existing behavior.

### SONNET (2 tasks)

**#18 — Add FocusNode management to all form pages**
Why Sonnet: Each form page needs FocusNodes created, attached to TextFields, linked with `TextInputAction.next` chains, and wired to `ensureVisible`. The number of fields varies (2 in LoginView, 7 in SignUpView, 5+ in AddMedicalRecordView). Sonnet can handle the per-page field mapping.

**#20 — Add search debouncing to SearchBloc + MessagesBloc**
Why Sonnet: Adding `restartable()` transformer requires understanding the bloc_concurrency package and how it interacts with existing event handlers. Also involves pre-computing filtered results in the handler instead of using getters. Moderate BLoC architecture work.

### HAIKU (2 tasks)

**#19 — Fix date picker delay pattern**
Why Haiku: Replace `Future.delayed(Duration(milliseconds: 80))` with `WidgetsBinding.instance.addPostFrameCallback((_) => showDatePicker(...))` in 5 files. Same fix each time.

**#21 — Fix AddAnnouncementView rebuild-per-keystroke**
Why Haiku: Replace `onChanged: (_) => setState(() {})` with a `ValueNotifier<bool>` + `ValueListenableBuilder` on the submit button. Focused single-file fix.

---

## Phase 4 — Transitions & Heroes

### OPUS (2 tasks)

**#25 — Staggered list entrances across 8+ views**
Why Opus: This requires creating a reusable staggered animation system (or a mixin/helper) and then applying it to MessagesView, NotificationsView, TeamView grid, SearchView, PlansView, JoinTeamView, IncomingRequestsView, and EventView bottom sheet. Each list type is different (ListView.builder, SliverGrid, Column with map). Opus can design a system that works for all of them without duplicating animation controller code in every file.

**#26 — HomeView section entrance stagger**
Why Opus: HomeView is the first screen users see after login. The entrance stagger orchestrates the event carousel and announcements section with overlapping `Interval`-based animations driven by a single `AnimationController`. The carousel itself is a `PageView.builder` with day grouping, and announcements have swipe sliders and edit mode. The stagger must compose with all existing animations. This is the highest-visibility animation in the app.

### SONNET (2 tasks)

**#23 — Add Hero animations (member, chat, event icons)**
Why Sonnet: Requires coordinating Hero tags between pairs of pages (TeamView <-> PlayerProfileView, MessagesView <-> ChatView, HomeView <-> DayEventsDetailView, PlansView <-> AddPlansView). Each pair needs matching tags and the destination page needs its Hero widget positioned correctly. Moderate cross-file coordination.

**#24 — Enhanced bottom sheet transitions**
Why Sonnet: Creating a custom `transitionAnimationController` for `showModalBottomSheet` with ScaleTransition. Applied to EventView and ChatView. Moderate animation work.

### HAIKU (1 task)

**#22 — Replace 3 MaterialPageRoute with AppPageRoute**
Why Haiku: Find three `MaterialPageRoute` usages in PlayerProfileView and MedicalRecordView, replace with `AppPageRoute`. Pure substitution.

---

## Phase 5 — BLoC & Performance

### SONNET (2 tasks)

**#27 — Fix add() chaining in 3 BLoCs**
Why Sonnet: Extracting load logic into private methods in AttendanceBloc, MedicalBloc, and TeamBloc requires understanding the async flow — which states are emitted, what the UI expects, and how to avoid the double-loading flicker. Not trivial refactoring.

**#33 — Fix silent error swallowing in PlansBloc/ChatBloc**
Why Sonnet: Designing proper error accumulation and emission strategy. The current `catch (_) {}` blocks need to be replaced with error tracking that surfaces failures to the UI without breaking the upload loop. Requires judgment about error UX.

### HAIKU (5 tasks)

**#28 — Memoize _groupByDay in HomeView**
Why Haiku: Move computation from build() to didUpdateWidget with a guard. Mechanical.

**#29 — Replace eager lists with ListView.builder (4 views)**
Why Haiku: In AttendanceView, GameSquadView, TeamStatsView (2 lists) — replace `...list.map()` inside Column with `ListView.builder`. Same pattern four times.

**#30 — Add RepaintBoundary to 3 widgets**
Why Haiku: Wrap `_BasketballTacticalBoard`, `LineChart`, and `_MessageBubble` in `RepaintBoundary()`. Three lines of code.

**#31 — Add const constructors throughout**
Why Haiku: Mechanical search-and-add across the codebase. Find `SizedBox(height: 16)` → add `const`. Find static `Text('...')` → add `const`. Find `EdgeInsets.all(16)` → add `const`. Repetitive.

**#32 — Remove 660 lines of dead code from GameDetailHistoryView**
Why Haiku: Delete commented-out code blocks. Trivial.

---

## Execution Order & Parallelism

```
PHASE 1 (Foundation):
  Opus: #4, #5, #6  (run in sequence — #5 depends on #4's API design)
  Sonnet: #1, #3, #11  (run in parallel — independent files)
  Haiku: #2, #7, #8, #9, #10  (run in parallel — all independent)
  
PHASE 2 (Buttons):
  [Depends on Phase 1 Opus completing #5]
  Opus: #16
  Sonnet: #15
  Haiku: #12, #13, #14  (run in parallel — independent button types)

PHASE 3 (Keyboard):
  [Depends on Phase 1 Opus completing #4]
  Opus: #17  (longest task — integrating mixin into 14 pages)
  Sonnet: #18, #20  (run in parallel)
  Haiku: #19, #21  (run in parallel)

PHASE 4 (Transitions):
  [Depends on Phase 1 Opus completing #6]
  Opus: #25, #26  (run in sequence — #25 creates the system, #26 uses it)
  Sonnet: #23, #24  (run in parallel)
  Haiku: #22

PHASE 5 (Performance):
  [No dependencies — can start as soon as Phase 1 Haiku finishes]
  Sonnet: #27, #33  (run in parallel)
  Haiku: #28, #29, #30, #31, #32  (run in parallel)
```

## Cost & Time Estimate

| Model | Tasks | Estimated Context Per Task | Total Tokens (approx) |
|---|---|---|---|
| Opus | 8 | 50K–100K tokens each | ~600K tokens |
| Sonnet | 12 | 30K–60K tokens each | ~500K tokens |
| Haiku | 13 | 10K–25K tokens each | ~200K tokens |

**Key insight:** Haiku handles 39% of the tasks (13/33) but uses only ~15% of the total token budget. Opus handles 24% of the tasks (8/33) but uses ~46% of the budget — that's where the quality investment pays off, because those 8 tasks define the systems the other 25 tasks build on.
