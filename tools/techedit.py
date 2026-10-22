# /// script
# requires-python = ">=3.10"
# dependencies = ["PySide6"]
# ///
"""Era and armour material editor for Sprocket's StreamingAssets.

Run: uv run tools/techedit.py [game folder]
     uv run tools/techedit.py --check   (round-trips every file through the writer)
"""
import json
import re
import sys
from decimal import Decimal
from pathlib import Path

from PySide6.QtCore import QDate, QRectF, Qt, QTimer, Signal
from PySide6.QtGui import QColor, QFont, QPainter, QPen
from PySide6.QtWidgets import (QApplication, QCheckBox, QDateEdit, QFormLayout, QHBoxLayout, QInputDialog, QLabel, QLineEdit, QListWidget,
                               QMainWindow, QMessageBox, QPushButton, QScrollArea, QSpinBox, QSplitter, QTableWidget,
                               QTableWidgetItem, QTabWidget, QVBoxLayout, QWidget)

args = [a for a in sys.argv[1:] if not a.startswith("--")]
GAME = Path(args[0]) if args else Path(__file__).resolve().parents[2]
ERAS = GAME / "Sprocket_Data" / "StreamingAssets" / "Eras"
TECH = GAME / "Sprocket_Data" / "StreamingAssets" / "Technology"
MAT_KEYS = ["rhaFactor", "hardness", "costMultiplier", "spallFactor", "density"]
COLS = ["file", "type", "date", "era", *MAT_KEYS, "RHA/kg"]
DATE = re.compile(r"(\d{4})\.(\d{2})\.(\d{2})")
LIMIT = 3000  # eras starting later (limit.json's 9999) are sentinels, kept off the timeline
TOP = 80


def load(path):
    # ponytail: regex strips trailing commas, would also hit ",}" inside a string; none of the game's files have one
    return json.loads(re.sub(r",(\s*[}\]])", r"\1", path.read_text(encoding="utf-8-sig")))


def num(v):
    if isinstance(v, float):  # 0.000005, not 5e-06
        s = format(Decimal(repr(v)), "f")
        return s if "." in s else s + ".0"
    return json.dumps(v, ensure_ascii=False)


def dump(d, depth=1):
    if not d:
        return "{}"
    pad = "\t" * depth
    lines = [f"{pad}{json.dumps(k, ensure_ascii=False)}: {dump(v, depth + 1) if isinstance(v, dict) else num(v)}"
             for k, v in d.items()]
    return "{\n" + ",\n".join(lines) + "\n" + "\t" * (depth - 1) + "}"


def year(date):
    m = DATE.fullmatch(date or "")
    return int(m[1]) + (int(m[2]) - 1) / 12 + (int(m[3]) - 1) / 365 if m else None


def entries(folder):
    out = []
    for p in sorted(folder.glob("*.json")):
        try:
            out.append({"path": p, "file": p.stem, "data": load(p), "dirty": False})
        except (ValueError, OSError) as e:
            print(f"Skipping {p.name}: {e}", file=sys.stderr)
    return out


def kind(v):
    return "true/false" if isinstance(v, bool) else "number" if isinstance(v, (int, float)) else \
        "text" if isinstance(v, str) else None


def parse_value(text, old=None):
    """Cell text as a property value of the same kind as old (any kind when old is None)."""
    if isinstance(old, str):
        return text
    try:
        v = json.loads(text)
    except ValueError:
        v = text
    if kind(v) is None or old is not None and kind(v) != kind(old):
        raise ValueError(f"expected {kind(old) or 'a number, true/false or text'}, got {text!r}")
    return v


def unique(base, taken, sep=" "):
    return next(n for n in (base, *(f"{base}{sep}{i}" for i in range(2, 1000))) if n not in taken)


def buttons(layout, pairs):
    for label, fn in pairs:
        b = QPushButton(label)
        b.clicked.connect(fn)
        layout.addWidget(b)
    return layout


def is_mat(e):
    return "rhaFactor" in e["data"].get("properties", {})


class Item(QTableWidgetItem):
    # the row's entry is a Python attribute: setData(UserRole, dict) would store a copy
    entry = None

    def __lt__(self, other):
        try:
            return float(self.text()) < float(other.text())
        except ValueError:
            return self.text().lower() < other.text().lower()


class Timeline(QWidget):
    picked = Signal(object)

    def __init__(self):
        super().__init__()
        self.ppy, self.eras, self.mats, self.marks, self.hits, self.sel = 14.0, [], [], [], [], None
        self.lo, self.hi = 1900, 2000

    def x(self, y):
        return 20 + (y - self.lo) * self.ppy

    def set(self, eras, mats):
        self.eras, self.mats = eras, mats
        ys = [year(e["data"].get("start")) for e in eras] + [year(m["data"].get("date")) for m in mats]
        ys = [y for y in ys if y is not None and 1000 < y < LIMIT] or [1900, 2000]  # 0000.01.01 = always
        self.lo, self.hi = int(min(ys)) - 2, int(max(ys)) + 12
        fm, lanes, self.marks = self.fontMetrics(), [], []
        for m in sorted(mats, key=lambda m: m["data"].get("date", "")):
            y = year(m["data"].get("date"))
            if y is None:
                continue
            x, w = self.x(max(y, self.lo)), self.fontMetrics().horizontalAdvance(m["file"]) + 10
            lane = next((i for i, end in enumerate(lanes) if end < x), len(lanes))
            lanes[lane:lane + 1] = [x + w]
            self.marks.append((m, QRectF(x, TOP + lane * 18, w, 16)))
        self.resize(int(self.x(self.hi)) + 20, TOP + len(lanes) * 18 + 10)
        self.update()

    def paintEvent(self, _):
        p, pal = QPainter(self), self.palette()
        p.fillRect(self.rect(), pal.base())
        self.hits = []
        eras = sorted(((year(e["data"].get("start")), e) for e in self.eras if year(e["data"].get("start"))),
                      key=lambda t: t[0])
        for i, (y, e) in enumerate(eras):
            if y >= LIMIT:
                continue
            end = min(eras[i + 1][0] if i + 1 < len(eras) else self.hi, self.hi)
            r = QRectF(self.x(y), 4, self.x(end) - self.x(y), 44)
            p.fillRect(r, QColor.fromHsv(i * 47 % 360, 110 if e["data"].get("playable") else 15, 235))
            p.setPen(QPen(Qt.black, 3 if e is self.sel else 1))
            p.drawRect(r)
            p.drawText(r.adjusted(4, 2, -2, -2), Qt.AlignLeft | Qt.AlignTop,
                       f"{e['data'].get('name', '')}\n{e['data'].get('start', '')}")
            self.hits.append((e, r))
        p.setPen(pal.text().color())
        step = next(s for s in (1, 2, 5, 10, 25, 50, 100) if s * self.ppy >= 40)
        for yr in range(self.lo - self.lo % step + step, self.hi + 1, step):
            x = self.x(yr)
            p.drawLine(int(x), 50, int(x), 56)
            p.drawText(int(x) - 20, 58, 40, 16, Qt.AlignCenter, str(yr))
        for m, r in self.marks:
            p.setPen(QColor(128, 128, 128, 90))
            p.drawLine(int(r.left()), 74, int(r.left()), int(r.top()))
            if m is self.sel:
                p.fillRect(r, pal.highlight())
            p.setPen(pal.highlightedText().color() if m is self.sel else pal.text().color())
            p.drawText(r.adjusted(4, 0, 0, 0), Qt.AlignVCenter, m["file"])
            self.hits.append((m, r))

    def mousePressEvent(self, ev):
        for e, r in self.hits:
            if r.contains(ev.position()):
                self.picked.emit(e)
                return

    def wheelEvent(self, ev):
        if not ev.modifiers() & Qt.ControlModifier:
            return ev.ignore()
        self.ppy = max(2.0, min(200.0, self.ppy * (1.2 if ev.angleDelta().y() > 0 else 1 / 1.2)))
        self.set(self.eras, self.mats)


class Main(QMainWindow):
    def __init__(self):
        super().__init__()
        self.setWindowTitle(f"Sprocket eras & materials — {GAME}")
        self.eras, self.techs, self.loading = entries(ERAS), entries(TECH), False
        self.timeline = Timeline()
        self.timeline.picked.connect(self.pick)
        scroll = QScrollArea()
        scroll.setWidget(self.timeline)
        self.tabs = QTabWidget()
        self.tabs.addTab(self.era_tab(), "Eras")
        self.tabs.addTab(self.mat_tab(), "Materials")
        split = QSplitter(Qt.Vertical)
        split.addWidget(scroll)
        split.addWidget(self.tabs)
        split.setSizes([300, 600])
        self.setCentralWidget(split)
        self.addToolBar("File").addAction("Save all (Ctrl+S)", self.save).setShortcut("Ctrl+S")
        self.statusBar().showMessage("Ctrl+wheel on the timeline zooms. Click a band or material to jump to it.")
        self.refresh()

    def mats(self):
        return [t for t in self.techs if is_mat(t)]

    def refresh(self, era=None):
        self.refresh_eras(era)
        self.refresh_mats()
        self.timeline.set(self.eras, self.mats())

    # ---- eras ----

    def era_tab(self):
        w = QWidget()
        h = QHBoxLayout(w)
        left = QVBoxLayout()
        self.era_list = QListWidget()
        self.era_list.currentRowChanged.connect(self.show_era)
        left.addWidget(self.era_list)
        left.addLayout(buttons(QHBoxLayout(), (("New (copy selected)", self.new_era), ("Delete", self.delete_era))))
        h.addLayout(left, 1)
        right = QVBoxLayout()
        self.form = QWidget()
        f = QFormLayout(self.form)
        self.f_file, self.f_name = QLineEdit(), QLineEdit()
        self.f_start = QDateEdit(displayFormat="yyyy.MM.dd", calendarPopup=True)
        self.f_playable = QCheckBox()
        self.f_medium, self.f_heavy = (QSpinBox(maximum=10_000_000, singleStep=1000, suffix=" kg") for _ in range(2))
        for label, wdg in (("File (.json)", self.f_file), ("Name", self.f_name), ("Start", self.f_start),
                           ("Playable", self.f_playable), ("Medium vehicle mass", self.f_medium),
                           ("Heavy vehicle mass", self.f_heavy)):
            f.addRow(label, wdg)
        for sig in (self.f_file.textEdited, self.f_name.textEdited, self.f_start.dateChanged,
                    self.f_playable.toggled, self.f_medium.valueChanged, self.f_heavy.valueChanged):
            sig.connect(self.edit_era)
        right.addWidget(self.form)
        split = QSplitter()
        box = QWidget()
        v = QVBoxLayout(box)
        v.addWidget(QLabel("Technologies in this era (from its start to the next era's)"))
        self.era_techs = QTableWidget(0, 3)
        self.era_techs.setHorizontalHeaderLabels(["file", "type", "date"])
        self.era_techs.itemChanged.connect(self.edit_tech)
        self.era_techs.sortByColumn(0, Qt.AscendingOrder)
        self.era_techs.currentItemChanged.connect(self.show_props)
        v.addWidget(self.era_techs)
        v.addLayout(buttons(QHBoxLayout(), (("New (copy selected)", self.new_tech), ("Delete", self.delete_tech))))
        split.addWidget(box)
        box = QWidget()
        v = QVBoxLayout(box)
        self.props_label = QLabel()
        v.addWidget(self.props_label)
        self.props = QTableWidget(0, 2)
        self.props.setHorizontalHeaderLabels(["property", "value"])
        self.props.horizontalHeader().setStretchLastSection(True)
        self.props.itemChanged.connect(self.edit_prop)
        v.addWidget(self.props)
        v.addLayout(buttons(QHBoxLayout(), (("Add property", self.add_prop), ("Remove property", self.remove_prop))))
        split.addWidget(box)
        right.addWidget(split, 1)
        h.addLayout(right, 3)
        return w

    def cur_era(self):
        r = self.era_list.currentRow()
        return self.eras[r] if 0 <= r < len(self.eras) else None

    def refresh_eras(self, sel=None):
        sel = sel or self.cur_era()
        self.eras.sort(key=lambda e: e["data"].get("start", ""))
        self.loading = True
        self.era_list.clear()
        for e in self.eras:
            d = e["data"]
            hidden = "" if d.get("playable") else "  (not playable)"
            self.era_list.addItem(f"{'* ' if e['dirty'] else ''}{d.get('start', '?')}  {d.get('name', '')}"
                                  f"  [{e['file']}.json]{hidden}")
        self.era_list.setCurrentRow(next((i for i, e in enumerate(self.eras) if e is sel), -1))
        self.loading = False
        self.show_era()

    def show_era(self, *_):
        if self.loading:
            return
        e = self.cur_era()
        self.timeline.sel = e
        self.timeline.update()
        self.form.setEnabled(e is not None)
        self.refresh_era_techs()
        if not e:
            return
        d, self.loading = e["data"], True
        self.f_file.setText(e["file"])
        self.f_name.setText(d.get("name", ""))
        self.f_start.setDate(QDate.fromString(d.get("start", ""), "yyyy.MM.dd"))
        self.f_playable.setChecked(bool(d.get("playable")))
        self.f_medium.setValue(int(d.get("mediumVehicleMass", 0)))
        self.f_heavy.setValue(int(d.get("heavyVehicleMass", 0)))
        self.loading = False

    def edit_era(self, *_):
        e = self.cur_era()
        if self.loading or not e:
            return
        e["file"] = self.f_file.text().strip()
        e["data"].update(name=self.f_name.text(), start=self.f_start.date().toString("yyyy.MM.dd"),
                         playable=self.f_playable.isChecked(), mediumVehicleMass=self.f_medium.value(),
                         heavyVehicleMass=self.f_heavy.value())
        e["dirty"] = True
        self.refresh(e)

    def new_era(self):
        src = self.cur_era()
        data = json.loads(json.dumps(src["data"])) if src else {
            "v": "0.0", "start": "1950.01.01", "playable": True, "mediumVehicleMass": 20000, "heavyVehicleMass": 40000}
        files = {e["file"] for e in self.eras}
        name = next(f"NewEra{i or ''}" for i in range(1000) if f"NewEra{i or ''}" not in files)
        data["name"] = name
        e = {"path": None, "file": name, "data": data, "dirty": True}
        self.eras.append(e)
        self.refresh(e)

    def era_entry(self, date):
        """The era a tech dated date belongs to; dates before the first era go to the first."""
        found = self.eras[0] if self.eras else None
        for e in self.eras:
            if e["data"].get("start", "") <= date:
                found = e
        return found

    def cur_tech(self):
        it = self.era_techs.currentItem()
        return it and it.entry

    def refresh_era_techs(self):
        era, cur, t = self.cur_era(), self.cur_tech(), self.era_techs
        was, self.loading = self.loading, True
        t.setSortingEnabled(False)
        t.setRowCount(0)
        font, shown = QFont(), []
        for e in self.techs:
            if not era or self.era_entry(e["data"].get("date", "")) is not era:
                continue
            shown.append(e)
            r = t.rowCount()
            t.insertRow(r)
            font.setBold(e["dirty"])
            e["ecells"] = []
            for c, text in enumerate((e["file"], e["data"].get("type", ""), e["data"].get("date", ""))):
                it = Item(text)
                it.entry = e
                it.setFont(font)
                t.setItem(r, c, it)
                e["ecells"].append(it)
        t.setSortingEnabled(True)
        t.resizeColumnsToContents()
        if any(cur is x for x in shown):
            t.setCurrentItem(cur["ecells"][0])
        self.loading = was
        self.show_props()

    def show_props(self, *_):
        if self.loading:
            return
        e, t = self.cur_tech(), self.props
        if e:
            self.timeline.sel = e
            self.timeline.update()
        self.props_label.setText(f"Properties of {e['file']}" if e else "Select a technology")
        self.loading = True
        t.setRowCount(0)
        for k, v in (e["data"].get("properties", {}) if e else {}).items():
            r = t.rowCount()
            t.insertRow(r)
            for c, text in enumerate((k, v if isinstance(v, str) else num(v))):
                it = QTableWidgetItem(text)
                it.setData(Qt.UserRole, k)
                t.setItem(r, c, it)
        t.resizeColumnToContents(0)
        self.loading = False

    def techs_changed(self):
        # deferred: the tables are rebuilt, and an itemChanged slot must not delete its own item
        def go():
            self.refresh_era_techs()
            self.refresh_mats()
            self.timeline.set(self.eras, self.mats())
        QTimer.singleShot(0, go)

    def edit_tech(self, item):
        if self.loading:
            return
        e = item.entry
        try:
            self.set_field(e, ["file", "type", "date"][item.column()], item.text().strip())
        except ValueError as err:
            QMessageBox.warning(self, "Invalid value", str(err))
        self.techs_changed()

    def edit_prop(self, item):
        e = self.cur_tech()
        if self.loading or not e:
            return
        props, key, text = e["data"].setdefault("properties", {}), item.data(Qt.UserRole), item.text()
        try:
            if item.column() == 0:
                text = text.strip()
                if not text or text in props:
                    raise ValueError(f"property name {text!r} is empty or already used")
                e["data"]["properties"] = {text if k == key else k: v for k, v in props.items()}
            else:
                props[key] = parse_value(text.strip(), props.get(key))
            e["dirty"] = True
        except ValueError as err:
            QMessageBox.warning(self, "Invalid value", str(err))
        QTimer.singleShot(0, self.show_props)
        self.techs_changed()

    def add_prop(self):
        e = self.cur_tech()
        if not e:
            return
        props = e["data"].setdefault("properties", {})
        key, ok = QInputDialog.getText(self, "Add property", "Name:")
        if not ok or not key.strip():
            return
        if key.strip() in props:
            return QMessageBox.warning(self, "Add property", f"{key.strip()} already exists.")
        text, ok = QInputDialog.getText(self, "Add property", f"{key.strip()} value (number, true/false or text):")
        if ok:
            props[key.strip()] = parse_value(text.strip())
            e["dirty"] = True
            self.techs_changed()

    def remove_prop(self):
        e, it = self.cur_tech(), self.props.currentItem()
        if e and it:
            e["data"]["properties"].pop(it.data(Qt.UserRole), None)
            e["dirty"] = True
            self.techs_changed()

    def copy_tech(self, src, date=None, new_type=False):
        # the same type may appear once per era, so a copy keeps it unless it's a new material
        data = json.loads(json.dumps(src["data"]))
        if new_type:
            data["type"] = unique(data.get("type", "tech"), {t["data"].get("type") for t in self.techs}, "")
        if date:
            data["date"] = date
        e = {"path": None, "file": unique(src["file"] + " copy", {t["file"] for t in self.techs}),
             "data": data, "dirty": True}
        self.techs.append(e)
        return e

    def new_tech(self):
        src, era = self.cur_tech(), self.cur_era()
        if not src:
            return QMessageBox.information(self, "New technology", "Select a technology to copy first.")
        e = self.copy_tech(src, era and era["data"].get("start"))
        self.refresh_era_techs()
        self.era_techs.setCurrentItem(e["ecells"][0])
        self.techs_changed()

    def delete_tech(self):
        e = self.cur_tech()
        if e and self.confirm_delete([e]):
            self.techs = [t for t in self.techs if t is not e]
            self.techs_changed()

    def delete_era(self):
        e = self.cur_era()
        if e and self.confirm_delete([e]):
            self.eras.remove(e)
            self.refresh()

    # ---- materials ----

    def mat_tab(self):
        w = QWidget()
        v = QVBoxLayout(w)
        self.table = QTableWidget(0, len(COLS))
        self.table.setHorizontalHeaderLabels(COLS)
        self.table.itemChanged.connect(self.edit_mat)
        self.table.sortByColumn(0, Qt.AscendingOrder)
        self.table.currentItemChanged.connect(self.mat_selected)
        v.addWidget(self.table)
        v.addLayout(buttons(QHBoxLayout(), (("New (copy selected)", self.new_mat),
                                            ("Delete selected", self.delete_mats))))
        return w

    def refresh_mats(self):
        t, cur = self.table, self.table.currentItem()
        cur = cur and cur.entry
        self.loading = True
        t.setSortingEnabled(False)
        t.setRowCount(0)
        mats = self.mats()
        for e in mats:
            r = t.rowCount()
            t.insertRow(r)
            e["cells"] = []
            for c, k in enumerate(COLS):
                it = Item()
                it.entry = e
                if k in ("era", "RHA/kg"):
                    it.setFlags(it.flags() & ~Qt.ItemIsEditable)
                t.setItem(r, c, it)
                e["cells"].append(it)
            self.fill_row(e)
        t.setSortingEnabled(True)
        t.resizeColumnsToContents()
        if any(cur is m for m in mats):
            t.setCurrentItem(cur["cells"][0])
        self.loading = False

    def fill_row(self, e):
        d, props, was = e["data"], e["data"].get("properties", {}), self.loading
        self.loading = True
        rha, dens = props.get("rhaFactor"), props.get("density")
        vals = {"file": e["file"], "type": d.get("type", ""), "date": d.get("date", ""),
                "era": (self.era_entry(d.get("date", "")) or {"data": {}})["data"].get("name", ""),
                "RHA/kg": f"{rha * 7850 / dens:.3f}" if isinstance(rha, (int, float)) and dens else "",
                **{k: num(props[k]) for k in MAT_KEYS if k in props}}
        font = QFont()
        font.setBold(e["dirty"])
        for k, it in zip(COLS, e["cells"]):
            it.setText(vals.get(k, ""))
            it.setFont(font)
        self.loading = was

    def edit_mat(self, item):
        if self.loading:
            return
        e, k = item.entry, COLS[item.column()]
        try:
            self.set_field(e, k, item.text().strip())
        except ValueError as err:
            QMessageBox.warning(self, "Invalid value", str(err))
        self.fill_row(e)
        if k in ("file", "date"):
            self.timeline.set(self.eras, self.mats())
        self.refresh_era_techs()

    def set_field(self, e, k, text):
        """Sets file, type, date or a material property from cell text; ValueError if it's invalid."""
        d = e["data"]
        props = d.setdefault("properties", {})
        if k == "file":
            if not text or "/" in text:
                raise ValueError("file name must be non-empty with no '/'")
            e["file"] = text
        elif k == "type":
            if not text:
                raise ValueError("type must be non-empty")
            d["type"] = text
        elif k == "date":
            if not DATE.fullmatch(text):
                raise ValueError("date must be yyyy.MM.dd")
            d["date"] = text
        elif not text:
            if k == "rhaFactor":
                raise ValueError("rhaFactor is required: it is what makes a tech an armour material")
            props.pop(k, None)
        else:
            props[k] = parse_value(text, 0)
        e["dirty"] = True

    def mat_selected(self, item, _=None):
        if item and not self.loading:
            self.timeline.sel = item.entry
            self.timeline.update()

    def selected_mats(self):
        out = []
        for it in self.table.selectedItems():
            e = it.entry
            if not any(x is e for x in out):
                out.append(e)
        return out

    def new_mat(self):
        sel = self.selected_mats()
        src = sel[0] if sel else next((t for t in self.techs if t["data"].get("type") == "rha"), None) or {
            "file": "New material", "data": {"v": "0.0", "id": "", "type": "newmat", "date": "1950.01.01",
                                             "properties": {"rhaFactor": 1.0, "density": 7850}}}
        e = self.copy_tech(src, new_type=True)
        self.refresh()
        self.table.setCurrentItem(e["cells"][0])
        self.table.scrollToItem(e["cells"][0])

    def delete_mats(self):
        sel = self.selected_mats()
        if sel and self.confirm_delete(sel):
            self.techs = [t for t in self.techs if not any(t is s for s in sel)]
            self.refresh()

    # ---- shared ----

    def pick(self, e):
        if any(e is x for x in self.eras):
            self.tabs.setCurrentIndex(0)
            self.era_list.setCurrentRow(next(i for i, x in enumerate(self.eras) if x is e))
        else:
            self.tabs.setCurrentIndex(1)
            self.table.setCurrentItem(e["cells"][0])
            self.table.scrollToItem(e["cells"][0])

    def confirm_delete(self, es):
        names = "\n".join(f"{e['file']}.json" for e in es)
        if QMessageBox.question(self, "Delete", f"Delete these? Saved files are renamed to .json.bak.\n\n{names}") \
                != QMessageBox.Yes:
            return False
        for e in es:
            if e["path"]:
                e["path"].rename(e["path"].with_suffix(".json.bak"))
        return True

    def save(self):
        errs = []
        for folder, es in ((ERAS, self.eras), (TECH, self.techs)):
            for e in es:
                if not e["dirty"]:
                    continue
                target = folder / f"{e['file']}.json"
                if not e["file"] or "/" in e["file"]:
                    errs.append(f"Bad file name: {e['file']!r}")
                elif target != e["path"] and target.exists():
                    errs.append(f"{target.name} already exists; rename the {folder.name} entry")
                else:
                    target.write_text(dump(e["data"]) + "\n", encoding="utf-8")
                    if e["path"] and e["path"] != target:
                        e["path"].unlink()
                    e["path"], e["dirty"] = target, False
        self.refresh()
        self.statusBar().showMessage("Saved." if not errs else "Saved with problems.", 5000)
        if errs:
            QMessageBox.warning(self, "Save", "\n".join(errs))

    def closeEvent(self, ev):
        if not any(e["dirty"] for e in self.eras + self.techs):
            return ev.accept()
        r = QMessageBox.question(self, "Unsaved changes", "Save before closing?",
                                 QMessageBox.Save | QMessageBox.Discard | QMessageBox.Cancel)
        if r == QMessageBox.Save:
            self.save()
            r = QMessageBox.Cancel if any(e["dirty"] for e in self.eras + self.techs) else r
        ev.ignore() if r == QMessageBox.Cancel else ev.accept()


def check():
    for folder in (ERAS, TECH):
        for e in entries(folder):
            assert json.loads(dump(e["data"])) == e["data"], e["path"]
    assert num(0.000005) == "0.000005" and num(1.0) == "1.0" and num(7850) == "7850"
    assert year("1914.07.28") > 1914.5 and year("bad") is None
    print("ok")


if __name__ == "__main__":
    if not ERAS.is_dir() or not TECH.is_dir():
        sys.exit(f"No Eras/Technology folders under {GAME}. Pass the game folder as an argument.")
    if "--check" in sys.argv:
        sys.exit(check())
    app = QApplication(sys.argv[:1])
    w = Main()
    w.resize(1400, 900)
    w.show()
    sys.exit(app.exec())
