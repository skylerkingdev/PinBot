# Sharing your setup

For VPin owners: how PinBot shares the tables you install, the views (POVs) you set and the script fixes you make, and
how to install what friends share on your own VPin. What PinBot reads and writes on your PC: [VPIN.md](VPIN.md).

Nothing is shared unless you say so. Your server's admin turns this on by choosing an updates channel; until then
PinBot asks nothing.

## What PinBot notices

| You... | PinBot asks about |
|---|---|
| install a new table, or a new version of one | a **table update**: which table, version, backglass and ROM, with their VPS links |
| change a table's view in VPX and exit | a **POV** for the mode your VPin plays in (cabinet, desktop or full single screen) |
| change a table's script (VPin Studio's script editor, the VPX editor, or a `.vbs` next to the table) | a **script fix**: the changed lines only |

About 45 seconds after the change, PinBot sends you a direct message. Installing several tables at once is one
message. The message shows what PinBot found and asks whether to share it:

- **Share**: post it now. **Always share**: post this and every later one of that kind without asking.
- **Not this time**: nothing is posted. **Don't ask again**: never share that kind, and stop asking.
- **Wrong match?**: PinBot matched your table to the wrong VPS entry. Pick the right table or backglass from the
  lists, or **Not on VPS**.

A message you don't answer closes after 7 days. Tables, POVs and fixes each have their own setting, so you can always
share tables but be asked about every camera tweak.

## The posts

- **Table updates** go to the updates channel: who updated what, from which version to which, and links straight to
  the authors' download pages for the exact table, backglass and ROM you run. If the VPS entry has a note (often a patch,
  such as a `.dif` to apply with a patcher), the post shows it.
- **POVs and script fixes** go to the setups channel (or the updates channel if there is none). A POV post shows the
  view settings as a block you can also paste under `[TableOverride]` in the table's `.ini` yourself. A fix post shows
  every changed line as a `diff`.

## Installing a friend's POV or fix

1. Press **Use this POV** or **Use this fix** under the post. Only you see the answer.
2. PinBot checks each of your VPins. It offers **Install on your VPin** only where the share fits exactly:
   - a **POV**: the same table, the same version, and the same view mode;
   - a **fix**: the same table and version, and **exactly the script** the fix was made from. If you have ever edited
     that table's script, or downloaded it from somewhere else, PinBot says "has a different script for this table"
     and installs nothing.
3. Press **Install**. Your VPin installs it the next time no table is running, usually within seconds.
4. You get a direct message when it's done, with an **Undo** button that puts back what you had.

How each is installed:

- **A POV** changes only that mode's view settings in the table's `.ini`. A copy of the file is kept first, and every
  other setting in it stays as it was.
- **A fix** is written as a `.vbs` file next to the table, which VPX uses instead of the script inside the `.vpx`. The
  `.vpx` itself is never changed. If you later install a new version of the table, PinBot moves the old fix aside
  (renamed `.vbs.pinbot-old`) so it doesn't override the new table's script.

**A script is a program.** VPX runs it with your full rights on the PC. Read the fix's changed lines before you install
it, and only install fixes from people you trust. PinBot refuses any fix whose new lines start programs, open files,
reach the network or change the registry, and never installs a fix during a competition on that game. Undo removes it.

## Settings

`/pinbot updates` (only you see the reply) shows your three settings and your recent changes with what became of each.
Change them with:

```
/pinbot updates share:<ask|always|never>   tables you install or update
/pinbot updates povs:<ask|always|never>    views you change
/pinbot updates fixes:<ask|always|never>   scripts you change
```

It also lets you correct the VPS links of a post you already shared: pick the post, then the right entries.

## What is never shared

Only what you choose to share is posted, and only what's listed above is sent. Never: your whole table script (only the
changed lines of a fix you share), your other table options (some set difficulty or ball count), your screen size or
player position, your high scores beyond competitions, or any file.
