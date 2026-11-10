#!/usr/bin/env python3
"""Run real two-process Unity sessions and validate CSV evidence. No third-party Python packages."""
import argparse
import csv
import json
import re
import shutil
import subprocess
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CASES = [("T1_lan", 0, "prediction"), ("T2_baseline", 1, "baseline"),
         ("T3_prediction", 1, "prediction"), ("T4_stress", 2, "stress"),
         ("T5_correction", 2, "correction")]


def rows(path):
    with path.open() as source:
        return list(csv.DictReader(source))


def field(details, name):
    match = re.search(r"(?:^|; )" + re.escape(name) + r"=([^;]+)", details)
    return match.group(1) if match else ""


def run_case(binary, name, profile, scenario, index):
    folder = ROOT / "media" / "checks" / name
    folder.mkdir(parents=True, exist_ok=True)
    common = [str(binary), "-labProfile", str(profile), "-labPort", str(17800 + index),
              "-labScenario", scenario, "-labOutput", str(folder),
              "-screen-width", "1280", "-screen-height", "800", "-screen-fullscreen", "0"]
    # Each peer gets its own log. Host lives longer so the client can complete and disconnect normally.
    host = subprocess.Popen(common + ["-labRole", "host", "-labDuration", "19", "-logFile", str(folder / "host.log")],
                            stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT)
    client = None
    try:
        time.sleep(2)
        client = subprocess.Popen(common + ["-labRole", "client", "-labDuration", "12", "-logFile", str(folder / "client.log")],
                                  stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT)
        client.wait(timeout=50)
        host.wait(timeout=30)
        assert client.returncode == 0 and host.returncode == 0, f"process exit: client={client.returncode}, host={host.returncode}"
    finally:
        for process in (client, host):
            if process is not None and process.poll() is None:
                process.terminate()
                process.wait(timeout=10)
    client_events, host_events = rows(folder / "client_events.csv"), rows(folder / "host_events.csv")
    assert any(row["event"] == "test_complete" for row in client_events), "client did not complete"
    assert any(row["event"] == "test_complete" for row in host_events), "host did not complete"
    for peer in ("host", "client"):
        log = (folder / f"{peer}.log").read_text(errors="replace")
        assert not re.search(r"Exception:|NullReferenceException|Error\]", log), f"runtime error in {peer}.log"
    confirmations = [row for row in client_events if row["event"] == "dash_confirmed"]
    accepted = [row for row in confirmations if field(row["details"], "result") == "1"]
    assert accepted, "no accepted dash on client"
    response = float(field(accepted[0]["details"], "response_ms"))
    confirmation = float(field(accepted[0]["details"], "confirmation_ms"))
    samples = [row for row in client_events if row["event"] == "sample"]
    assert any(field(row["details"], "spawned") == "2" for row in samples), "client never saw two players"
    assert any(row["event"] == "server_dash" and "owner=1;" in row["details"] for row in host_events), "host did not simulate client dash"
    assert int(field(samples[-1]["details"], "ack")) > 200, "acknowledgement stream stalled"
    # Reliable delivery under random loss pauses ACKs transiently. Check the bounded buffer,
    # a final window, and continued progress instead of an arbitrary single-frame cutoff.
    pending_counts = [int(field(row["details"], "pending")) for row in samples]
    assert max(pending_counts) < 240, "saved input buffer reached its capacity"
    assert sum(pending_counts[-6:]) / len(pending_counts[-6:]) < 60, "sustained input backlog exceeds two seconds"
    assert int(field(samples[-1]["details"], "ack")) > int(field(samples[-6]["details"], "ack")), "ACKs stopped in the final window"
    assert field(samples[-1]["details"], "pos") == field(samples[-1]["details"], "authority"), "client did not converge after neutral input"
    if profile == 1:
        assert confirmation >= 180, f"100 ms one-way emulation did not apply: confirmation={confirmation}"
        if scenario == "baseline":
            assert response >= 180, f"baseline reacted too soon: {response}"
        else:
            assert response < 100, f"prediction response too slow: {response}"
    corrections = [row for row in client_events if row["event"] == "reconcile"]
    rejected = [row for row in host_events if row["event"] == "server_dash" and "owner=1;" in row["details"] and field(row["details"], "result") == "2"]
    if scenario in ("stress", "correction"):
        assert rejected, "server did not reject cooldown violation"
        assert any(float(field(row["details"], "error")) > 7 for row in corrections), "wrong local dash was not reconciled"
    screenshot = folder / (scenario + ".png")
    assert screenshot.exists(), "screenshot not captured"
    result = dict(test=name, profile=profile, scenario=scenario, response_ms=response,
                  confirmation_ms=confirmation, corrections=len(corrections),
                  server_rejections=len(rejected), final_ack=int(field(samples[-1]["details"], "ack")),
                  final_pending=int(field(samples[-1]["details"], "pending")), screenshot=str(screenshot.relative_to(ROOT)), passed=True)
    print(json.dumps(result, ensure_ascii=False), flush=True)
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--binary", type=Path, default=ROOT / "Builds/PredictionLab.app/Contents/MacOS/Game_PRMICSS")
    parser.add_argument("--case", choices=[case[0] for case in CASES])
    args = parser.parse_args()
    binary = args.binary.resolve()
    if not binary.is_file():
        raise SystemExit(f"Build first: missing executable {binary}")
    results = []
    for index, (name, profile, scenario) in enumerate(CASES):
        if args.case and args.case != name:
            continue
        print(f"Running {name}...", flush=True)
        results.append(run_case(binary, name, profile, scenario, index))
        checkpoint = ROOT / "media/checks" / ("results.json" if not args.case else args.case + "_result.json")
        checkpoint.write_text(json.dumps(results, ensure_ascii=False, indent=2) + "\n")
    output = ROOT / "media/checks" / ("results.json" if not args.case else args.case + "_result.json")
    output.write_text(json.dumps(results, ensure_ascii=False, indent=2) + "\n")
    if not args.case:
        publish_report(results)


def publish_report(results):
    """Keep the submitted screenshots and matrix tied to this exact run."""
    media = ROOT / "docs/media"
    media.mkdir(parents=True, exist_ok=True)
    screenshots = {
        "baseline_no_prediction.png": "T2_baseline/baseline.png",
        "prediction_enabled.png": "T3_prediction/prediction.png",
        "correction_example_before.png": "T5_correction/correction_before.png",
        "correction_example.png": "T5_correction/correction.png",
    }
    for target, source in screenshots.items():
        shutil.copy2(ROOT / "media/checks" / source, media / target)
    labels = [
        ("Без CSP: 100 мс / 0%", "baseline_no_prediction.png",
         "Действие показано после серверного ответа. Отклик и ответ сервера почти совпадают."),
        ("С CSP: 100 мс / 0%", "prediction_enabled.png",
         "Локальный отклик быстрый, серверное подтверждение приходит позже. Сравни числа в статистике."),
        ("Ошибочное предсказание: 175 мс / 4%, до коррекции", "correction_example_before.png",
         "После F локальная позиция отличается от серверной. Ошибочный рывок выполняется во время кулдауна."),
        ("Серверная коррекция: 175 мс / 4%, после ACK", "correction_example.png",
         "Сервер отказал по кулдауну. Есть коррекция и отказ, позиции снова совпадают."),
    ]
    captions = ["# Подписанные снимки упрощённой демонстрации\n",
                "Снимки получены из реальных Host/Client-процессов. "
                "В каждом сценарии клиент идёт вперёд, затем делает рывок. "
                "В сценарии коррекции дополнительно выполняется неверный рывок F.\n"]
    for title, filename, caption in labels:
        captions.append(f"## {title}\n\n{caption}\n\n![{title}]({filename})\n")
    captions.append("Все исходные CSV, PNG и результаты: [media/checks](../../media/checks/). "
                    "Числа: [матрица](../Prediction_Test_Matrix.md). "
                    "Замеры на снимках коррекции относятся к последнему действию; "
                    "в матрице — к первому разрешённому рывку.\n")
    (media / "README.md").write_text("\n".join(captions))

    descriptions = [
        ("Без задержки", "Ходьба и рывок", "Два игрока, репликация, совпадение позиций"),
        ("100 мс / 0%", "Рывок без CSP", "Отклик после ответа сервера"),
        ("100 мс / 0%", "Рывок с CSP", "Отклик < 100 мс, подтверждение позже"),
        ("175 мс / 4%", "Движение и неверный рывок", "Отказ по кулдауну, коррекция > 7 единиц"),
        ("175 мс / 4%", "Намеренное расхождение", "Клиент возвращён к серверному состоянию"),
    ]
    lines = ["# Матрица сетевых проверок\n",
             "Упрощённая Unity-версия, проверена 3 ноября 2026 на macOS, "
             "Unity 6000.5.0b2. Два самостоятельных процесса в каждом тесте. **5/5 PASS**.\n",
             "| Тест | Профиль каждого узла | Действие | Ожидание | Факт | Артефакты |",
             "| --- | --- | --- | --- | --- | --- |"]
    for result, (profile, action, expected) in zip(results, descriptions):
        name = result["test"]
        fact = (f"PASS; отклик {result['response_ms']:.1f} мс, "
                f"подтверждение {result['confirmation_ms']:.1f} мс; "
                f"коррекций {result['corrections']}, отказов {result['server_rejections']}")
        links = (f"[PNG](../{result['screenshot']}), "
                 f"[клиент](../media/checks/{name}/client_events.csv), "
                 f"[сервер](../media/checks/{name}/host_events.csv)")
        lines.append(f"| {name} | {profile} | {action} | {expected} | {fact} | {links} |")
    lines += ["\n## Что проверяет скрипт\n",
              "Оба процесса завершились штатно, клиент видит двух игроков, сервер обработал "
              "рывок удалённого игрока, ACK продвигаются и в конце проверки. Очередь всегда < 240, "
              "а среднее последних 6 замеров pending < 60 (две секунды ввода), "
              "после нейтрального ввода позиции совпадают. При 100 мс подтверждение >= 180 мс, "
              "без CSP отклик >= 180 мс, с CSP < 100 мс. При ошибке сервер отклоняет действие "
              "по кулдауну, клиент регистрирует исправление > 7 единиц и продолжает обмен. "
              "Проверено отсутствие исключений в runtime-логах.\n",
              "| Тест | Последний ACK | Ожидающих вводов | Ошибка в конце |",
              "| --- | ---: | ---: | --- |"]
    for result in results:
        lines.append(f"| {result['test']} | {result['final_ack']} | {result['final_pending']} | 0 по координатам CSV |")
    lines += ["\nЗадержка задаётся на отправке **каждого** узла. Надёжная доставка, тики и очереди "
              "добавляют время к базовым 200/350 мс туда-обратно. Потери случайны, числа одного "
              "запуска не являются средними. Pending в конце не обязан быть нулём: "
              "нейтральные команды продолжают отправляться. Клиент тестируется 12 секунд.\n",
              "Замеры в таблице относятся к **первому разрешённому рывку**. "
              "В снимках T4/T5 может отображаться последующий отказ; сопоставляй по `seq` в CSV. "
              "Разница до 0,1 мс между близкими замерами связана с порядком чтения таймера.\n",
              "Повторить: `python3 Tools/run_network_checks.py`. "
              "Перед проверкой пересобрать: Unity → Practical 4 → Build macOS and validate simulation. "
              "Сборка также проверяет 7 правил симуляции.\n",
              "[JSON](../media/checks/results.json) · [Подписанные снимки](media/README.md) · "
              "[Реализация](Prediction_Implementation.md) · [Сборка](Build_Verification.txt)\n"]
    (ROOT / "docs/Prediction_Test_Matrix.md").write_text("\n".join(lines))


if __name__ == "__main__":
    main()
