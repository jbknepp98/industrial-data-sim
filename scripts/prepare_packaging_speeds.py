#!/usr/bin/env python3
"""Prepare additive speed tags from a packaging model; never publish here.

Local Boolean controls mirror the immutable source schedule because cross-session
live dependencies are not implemented. Existing model tags are never rewritten.
"""
import copy

MINUTE = 60000


def build_model(source, prefix, session_id):
    """Accept a validated model from prepare_packaging_demo.build_model.

    The resulting model must also pass the normal schema/runtime validation
    before admission. This helper is not a general untrusted-model loader.
    """
    generators = {item['tag']: item for item in source['generators']}
    sku = generators[prefix + '.Production.SKU.String']['steps']
    ready = generators[prefix + '.Mixer.Ready.Boolean']['steps']
    # Resolve both timelines at their actual boundaries, not via sampled values.
    def boundaries(steps):
        result = []
        elapsed = 0
        for step in steps:
            result.append((elapsed, elapsed + step['durationMs'], step['value']))
            elapsed += step['durationMs']
        return result
    sku_intervals, ready_intervals = boundaries(sku), boundaries(ready)
    if sku_intervals[-1][1] != ready_intervals[-1][1]:
        raise ValueError('speed.schedule_length: SKU and readiness schedules must cover the same duration.')
    edges = sorted({point for intervals in (sku_intervals, ready_intervals)
                    for start, end, _ in intervals for point in (start, end)})
    model = copy.deepcopy(source)
    model['session']['sessionId'] = session_id
    model['session']['outputTags'] = []
    model['generators'] = []
    for cell, nominal, seed in [('A', 120, 431), ('B', 95, 719)]:
        control = prefix + '.Packaging' + cell + '.SpeedControl.Boolean'
        speed = prefix + '.Packaging' + cell + '.Speed.PackagesPerMinute'
        steps = []
        for start, end in zip(edges, edges[1:]):
            selected = next(value for a, b, value in sku_intervals if a <= start < b)
            enabled = next(value for a, b, value in ready_intervals if a <= start < b)
            value = enabled and selected == 'SKU-' + cell
            if steps and steps[-1]['value'] == value:
                steps[-1]['durationMs'] += end - start
            else:
                steps.append({'value': value, 'durationMs': end - start})
        def staircase(values):
            return {'kind': 'staircase', 'afterSteps': 'holdLast', 'steps': [
                {'value': value, 'durationMs': minutes * MINUTE} for value, minutes in values]}
        def random_hold(low, high, minutes, offset):
            return {'kind': 'randomIntegerHold', 'minimum': low, 'maximum': high,
                    'seed': seed + offset, 'durationMs': minutes * MINUTE,
                    'holdDurationRangeMs': {'minimum': 2 * MINUTE, 'maximum': 5 * MINUTE},
                    'adjacentValues': 'requireChange', 'afterDuration': 'holdLast'}
        running = [staircase([(nominal // 3, 2), (nominal * 2 // 3, 2), (nominal - 10, 2)]),
                   random_hold(nominal - 10, nominal + 5, 40, 0),
                   random_hold(nominal // 2, nominal * 2 // 3, 5, 1),
                   random_hold(nominal - 10, nominal + 5, 45, 2),
                   random_hold(nominal // 2, nominal * 2 // 3, 5, 3),
                   random_hold(nominal - 10, nominal + 5, 180, 4)]
        def sequence(patterns):
            return {'kind': 'sequence', 'afterSequence': 'holdLast',
                    'steps': [{'pattern': pattern} for pattern in patterns]}
        model['session']['outputTags'].extend([
            {'name': control, 'valueType': 'boolean'}, {'name': speed, 'valueType': 'number'}])
        model['generators'].extend([
            {'tag': control, 'kind': 'booleanTimeline', 'afterSteps': 'holdLast', 'steps': steps},
            {'tag': speed, 'kind': 'booleanSwitch', 'triggerTag': control, 'onChange': 'restartBranch',
             'whenFalse': sequence([staircase([(0, 1)])]), 'whenTrue': sequence(running)}])
    return model
