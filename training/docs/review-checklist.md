# Review checklist

The build checks format, captions, the transcript and sensitive strings automatically. A person still watches each new
or rebuilt video once before it is published. Watch it with the sound on, then skim it with the sound off and captions
on.

## Content

- [ ] It does what the title says, for the role on the card, in the order the user guide describes.
- [ ] Every narrated line matches what happens on screen, including button names and outcomes.
- [ ] Outcomes shown are correct for the data. For example, the threshold result is what policy says for that value.
- [ ] Nothing on screen is out of date or contradicts the [user guides](../../docs/guides).
- [ ] The recap matches what was done.

## Privacy

- [ ] Only synthetic demo people, places and organizations appear. The footer reads "Synthetic demonstration data
      only".
- [ ] No password, code, recovery code, QR code, key, token or connection string is visible in any frame. The sign-in
      steps happen off camera.
- [ ] No real e-mail address, phone number or personal name appears, including in browser chrome or notifications.

## Accessibility

- [ ] Captions are in sync (within about half a second), readable, and at most two lines.
- [ ] The transcript reads as a complete account of the video for someone who cannot see or hear it.
- [ ] The narration is clear at normal speed, and nothing relies on sound alone.
- [ ] Highlights and zooms are smooth, nothing flashes, and the cursor is easy to follow.
- [ ] Text in the picture is legible at full screen and on a laptop.

## Technical

- [ ] `npm test` passes, including `outputs.spec.ts` for this video.
- [ ] The video plays from the start in a browser (`<video>` with the `.vtt` track) and in VLC or Windows Media Player
      with the `.srt`.

Record the review (reviewer, date, MERIT commit from `timeline.json`) wherever the program keeps its training records.
