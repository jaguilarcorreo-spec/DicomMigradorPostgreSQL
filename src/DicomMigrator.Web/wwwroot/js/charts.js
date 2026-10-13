// Gráficas de la ficha de migración (CompositionCharts.razor). El SVG se genera en el
// servidor; aquí va solo lo que conviene hacer en el navegador, sin viajes al servidor:
//  · el detalle al pasar el ratón (o al llegar con el teclado) por una porción, una fila de
//    la leyenda o una barra: lee el JSON de su atributo data-tip;
//  · el resaltado cruzado leyenda ↔ porción de cada tarta (data-key);
//  · llevar la vista por meses, cuando se desplaza en horizontal, hasta lo más reciente.
window.moveCharts = {
    scrollToEnd: (el) => { if (el) el.scrollLeft = el.scrollWidth; }
};

(() => {
    let tip = null, current = null;
    const esc = s => String(s ?? '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));

    function tipEl() {
        if (!tip) {
            tip = document.createElement('div');
            tip.className = 'cmp-tip';
            tip.hidden = true;
            document.body.appendChild(tip);
        }
        return tip;
    }

    function show(el, hotKey, x, y) {
        let d;
        try { d = JSON.parse(el.getAttribute('data-tip')); } catch { return; }
        const rows = (d.r || []).map(([code, color, value, key]) =>
            `<div class="t2${key === hotKey ? ' hot' : ''}"><span><span class="cmp-sw" style="background:${esc(color)}"></span>${esc(code)}</span><span>${esc(value)}</span></div>`).join('');
        const t = tipEl();
        t.innerHTML = `<b>${esc(d.t)}</b> · ${esc(d.s)}${rows}${d.h ? `<div class="t3">${esc(d.h)}</div>` : ''}`;
        t.hidden = false;
        place(x, y);
    }

    function place(x, y) {
        const t = tipEl();
        t.style.left = Math.max(8, Math.min(x + 14, innerWidth - t.offsetWidth - 8)) + 'px';
        t.style.top = Math.max(8, Math.min(y + 14, innerHeight - t.offsetHeight - 8)) + 'px';
    }

    function highlight(el, on) {
        const key = el.getAttribute('data-key');
        const card = el.closest('.cmp-pie-card');
        if (!key || !card) return;
        card.classList.toggle('hovering', on);
        card.querySelectorAll('[data-key]').forEach(n => n.classList.toggle('hot', on && n.getAttribute('data-key') === key));
    }

    function hide() {
        if (current) highlight(current, false);
        current = null;
        if (tip) tip.hidden = true;
    }

    document.addEventListener('mousemove', e => {
        const el = e.target.closest ? e.target.closest('.cmp-root [data-tip]') : null;
        if (!el) { if (current) hide(); return; }
        if (el !== current) { if (current) highlight(current, false); current = el; highlight(el, true); }
        const seg = e.target.closest('[data-mod]');
        show(el, seg ? seg.getAttribute('data-mod') : null, e.clientX, e.clientY);
    });
    document.addEventListener('focusin', e => {
        const el = e.target.closest ? e.target.closest('.cmp-root [data-tip]') : null;
        if (!el) return;
        if (current) highlight(current, false);
        current = el; highlight(el, true);
        const r = el.getBoundingClientRect();
        show(el, null, r.right - 6, r.top - 6);
    });
    document.addEventListener('focusout', hide);
    document.addEventListener('click', hide, true);       // al pulsar (p. ej. bajar a meses) la barra se redibuja
    document.addEventListener('scroll', hide, true);
})();
