"""Checks a lap-times-record recording.

For every car: when it crossed each sector start (interpolated between the two frames around the crossing), the
sector times and their sum per lap, iRacing's own last lap time for that lap and how long after the line it appeared,
and whether the lap had time on pit road or off track. Prints a table per car and a summary.

    python scripts/lap-times-check.py docs/recordings/lap-times-....jsonl
"""

from __future__ import annotations

import json
import sys
from collections import defaultdict

JUMP = 0.2  # more lap distance than this between two frames is a jump (tow, reset), not driving
GAP = 0.5  # seconds between two frames of a car: longer is a gap in the data


def read(path):
    header, frames = None, []
    with open(path, encoding='utf-8') as f:
        for line in f:
            try:
                row = json.loads(line)
            except ValueError:
                continue  # the last line of a recording that was cut off
            if row.get('type') == 'header':
                header = row
            elif row.get('type') == 'f':
                frames.append(row)
    return header, frames


def crossings(prev, cur, starts):
    """The sector starts crossed between two readings of a car, with the interpolated time: [(sector, time)]."""
    t0, p0 = prev
    t1, p1 = cur
    moved = (p1 - p0) % 1.0
    if moved <= 0 or moved > JUMP or t1 - t0 > GAP:
        return None if moved > JUMP or t1 - t0 > GAP else []
    found = []
    for i, s in enumerate(starts):
        ahead = (s - p0) % 1.0
        if 0 < ahead <= moved:
            found.append((i, t0 + (t1 - t0) * ahead / moved))
    return sorted(found, key=lambda x: x[1])


def main(path):
    header, frames = read(path)
    if header is None or not frames:
        print('No header or no frames.')
        return 1
    starts = header.get('sectorStarts') or [0.0]
    print(f"{header['track']['name']} - {header['track']['config']} (Track No. {header['track']['id']}), "
          f"session {header['session']['num']} {header['session']['type']}, iRacing {header.get('iracingBuild')}")
    print(f'Sector starts: {starts}  ({len(frames)} frames, {frames[-1]["t"] - frames[0]["t"]:.0f} s)')
    if starts[0] != 0:
        print('NOTE: the first sector does not start at 0: the line is not a sector start.')

    last = {}  # car -> (t, pct)
    marks = defaultdict(list)  # car -> [(sector, time)] or None for a break
    flags = defaultdict(lambda: defaultdict(set))  # car -> lap -> {'pit', 'off'}
    times = defaultdict(list)  # car -> [(t, lapsCompleted, lastLapTime, bestLapTime, bestLapNum)]
    for fr in frames:
        t = fr['t']
        for num, done, pct, pit, surface, lap_time, best, best_num in fr['cars']:
            if num in last:
                found = crossings(last[num], (t, pct), starts)
                if found is None:
                    marks[num].append(None)
                else:
                    marks[num].extend(found)
            last[num] = (t, pct)
            if pit:
                flags[num][done].add('pit')
            if surface == 0:
                flags[num][done].add('off')
            if not times[num] or times[num][-1][1:] != (done, lap_time, best, best_num):
                times[num].append((t, done, lap_time, best, best_num))

    diffs, delays = [], []
    for num in sorted(marks, key=lambda n: (len(n), n)):
        print(f'\n#{num}')
        print('  lap  ' + ''.join(f'S{i + 1:<9}' for i in range(len(starts))) + 'sum        iRacing    diff     after line  marks')
        # Laps: from one crossing of sector 0 (the line) to the next, with every sector in between and no break.
        seq = marks[num]
        line_at = [k for k, m in enumerate(seq) if m is not None and m[0] == 0]
        for a, b in zip(line_at, line_at[1:]):
            part = seq[a:b + 1]
            if None in part or [m[0] for m in part[:-1]] != list(range(len(starts))):
                continue
            sectors = [part[i + 1][1] - part[i][1] for i in range(len(starts))]
            total = sum(sectors)
            crossed = part[-1][1]
            # The lap that just ended: the lap count after the line minus one; iRacing's time for it is the first new
            # last lap time after the line.
            after = [x for x in times[num] if x[0] >= crossed - 0.05]
            done_after = after[0][1] if after else None
            lap_no = done_after if done_after is not None else '?'
            before = [x for x in times[num] if x[0] < crossed - 0.05]
            previous = before[-1][2] if before else None
            change = next((x for x in after if x[2] != previous and x[0] <= crossed + 5), None)
            if change:
                official, delay = change[2], change[0] - crossed
            else:  # the same time as the lap before (or none): the value a moment after the line, no delay known
                settled = [x for x in times[num] if x[0] <= crossed + 1]
                official, delay = (settled[-1][2] if settled else None), None
            lap_flags = sorted(flags[num].get(done_after - 1, set()) if isinstance(done_after, int) else [])
            diff = None if official is None else total - official
            if diff is not None and not lap_flags:
                diffs.append(abs(diff))
            if delay is not None:
                delays.append(delay)
            cells = ''.join(f'{s:<10.3f}' for s in sectors)
            print(f'  {lap_no!s:<4} {cells}{total:<10.3f} {official if official is not None else "-":<10} '
                  f'{"-" if diff is None else f"{diff:+.3f}":<8} {"-" if delay is None else f"{delay:.2f} s":<11} {" ".join(lap_flags)}')
        if times[num]:
            t, done, lap_time, best, best_num = times[num][-1]
            print(f'  at the end: laps completed {done}, last {lap_time}, best {best} (lap {best_num})')

    print('\nSummary')
    if diffs:
        print(f'  clean laps: {len(diffs)}, |sum of sectors - iRacing| max {max(diffs):.3f} s, mean {sum(diffs) / len(diffs):.3f} s')
    if delays:
        delays.sort()
        print(f'  iRacing\'s last lap time appears {delays[0]:.2f} to {delays[-1]:.2f} s after the line (median {delays[len(delays) // 2]:.2f} s)')
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1]))
