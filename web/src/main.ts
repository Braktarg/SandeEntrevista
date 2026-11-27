import { bootstrapApplication } from '@angular/platform-browser';
import { provideAnimationsAsync } from '@angular/platform-browser/animations/async';
import { HttpClient, HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { Component, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatAutocompleteModule } from '@angular/material/autocomplete';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTableModule } from '@angular/material/table';
import { Subject, catchError, firstValueFrom, of, switchMap, timer } from 'rxjs';

interface Supplier { idEntidad: number; abreviacion: string; razonSocial: string; }
interface Brand { idMarca: number; marca: string; }
interface Principal { idProducto: number; codigo: string; proveedor: string; producto: string; }
interface Row {
  idProducto: number; idEntidad: number; seccion: string; categoria: string; codigo: string;
  code: string; producto: string; nClientes: number; tipo: string; situacion: string;
  fui: string; marca: string; stockTotal: number;
  draft?: string; destination?: Principal; options?: Principal[];
}

@Component({
  selector: 'app-root',
  imports: [FormsModule, MatAutocompleteModule, MatButtonModule, MatButtonToggleModule,
    MatCardModule, MatFormFieldModule, MatInputModule, MatProgressBarModule, MatTableModule],
  templateUrl: './app.html',
})
class App {
  private readonly http = inject(HttpClient);
  private readonly supplierQuery = new Subject<string>();
  private readonly brandQuery = new Subject<string>();
  private readonly principalQuery = new Subject<{ row: Row; text: string }>();
  readonly types = ['G', 'P', 'R', 'S'];
  readonly situations = ['#', 'A', 'C', 'D', 'I'];
  readonly columns = [
    { key: 'seccion', label: 'Sección' }, { key: 'categoria', label: 'Categoría' },
    { key: 'codigo', label: 'Código' }, { key: 'code', label: 'Code' },
    { key: 'producto', label: 'Producto' }, { key: 'nClientes', label: '#C' },
    { key: 'tipo', label: 'Tipo' }, { key: 'situacion', label: 'Situación' },
    { key: 'fui', label: 'FUI' }, { key: 'marca', label: 'Marca' },
  ] as const;
  readonly displayed = [...this.columns.map(column => column.key), 'codigoN', 'destination'];
  supplierText = '';
  supplier?: Supplier;
  suppliers: Supplier[] = [];
  brandText = '';
  brands: Brand[] = [];
  minClients: number | null = 0;
  selectedTypes: string[] = [];
  selectedSituations: string[] = [];
  rows: Row[] = [];
  total = 0;
  busy = false;
  searched = false;
  error = '';
  notice = '';
  private lastQuery?: Record<string, string | number>;

  // Cada cambio cancela la consulta anterior del mismo buscador para evitar sugerencias obsoletas.
  constructor() {
    this.supplierQuery.pipe(switchMap(q => timer(220).pipe(switchMap(() =>
      this.http.get<Supplier[]>('/api/proveedores', { params: { q } }).pipe(catchError(e => this.lookupError(e)))))))
      .subscribe(items => this.suppliers = items);
    this.brandQuery.pipe(switchMap(q => timer(220).pipe(switchMap(() =>
      this.http.get<Brand[]>('/api/marcas', {
        params: this.supplier ? { q, idEntidad: this.supplier.idEntidad } : { q },
      }).pipe(catchError(e => this.lookupError(e)))))))
      .subscribe(items => this.brands = items);
    this.principalQuery.pipe(switchMap(({ row, text }) => timer(220).pipe(switchMap(() =>
      this.http.get<Principal[]>('/api/productos/principales/buscar', {
        params: { q: text, excluirId: row.idProducto },
      }).pipe(catchError(e => this.lookupError(e)))),
      ))).subscribe(items => {
        const request = this.activePrincipal;
        if (request && request.row.draft === request.text) request.row.options = items;
      });
  }

  private activePrincipal?: { row: Row; text: string };
  private lookupError(error: unknown) {
    this.error = this.message(error);
    return of([]);
  }

  private message(error: unknown) {
    return error instanceof HttpErrorResponse && typeof error.error?.message === 'string'
      ? error.error.message : 'No se pudo completar la operación. Revise la conexión e intente nuevamente.';
  }

  findSuppliers(text: string) { this.supplierQuery.next(text); }
  supplierLabel = (value: Supplier | string) => typeof value === "string" ? value : value?.abreviacion ?? "";
  principalLabel = (value: Principal | string) => typeof value === "string" ? value : value?.codigo ?? "";
  editSupplier(text: string | Supplier) {
    if (typeof text !== "string") return;
    this.supplierText = text;
    this.supplier = undefined;
    this.findSuppliers(text);
    this.findBrands(this.brandText);
  }
  pickSupplier(supplier: Supplier) {
    this.supplier = supplier;
    this.supplierText = supplier.abreviacion;
    this.findBrands(this.brandText);
  }
  findBrands(text: string) { this.brandQuery.next(text); }
  editCode(row: Row, text: string | Principal) {
    if (typeof text !== "string") return;
    row.draft = text;
    row.destination = undefined;
    row.options = [];
    const request = { row, text };
    this.activePrincipal = request;
    this.principalQuery.next(request);
  }
  // El texto digitado es un borrador. Solo al elegir una opción se obtiene el ID del destino.
  pickCode(row: Row, destination: Principal) {
    row.draft = destination.codigo;
    row.destination = destination;
  }

  get assigned() { return this.rows.filter(row => row.destination).length; }
  get invalidDrafts() { return this.rows.some(row => row.draft?.trim() && !row.destination); }

  async search() {
    if (!this.supplier || this.busy) return;
    this.notice = '';
    const minimum = this.minClients ?? 0;
    if (!Number.isInteger(minimum) || minimum < 0) {
      this.error = 'Ingrese un número entero de clientes mayor o igual a cero.';
      return;
    }
    await this.load({
      idEntidad: this.supplier.idEntidad, marca: this.brandText.trim(), minClientes: minimum,
      tipos: this.selectedTypes.join(','), situaciones: this.selectedSituations.join(','),
    });
  }

  private async load(params: Record<string, string | number>) {
    this.busy = true;
    this.error = '';
    try {
      const result = await firstValueFrom(this.http.get<{ total: number; items: Row[] }>(
        '/api/productos/consulta', { params },
      ));
      this.rows = result.items;
      this.total = result.total;
      this.searched = true;
      this.lastQuery = params;
    } catch (error) {
      this.error = this.message(error);
    } finally {
      this.busy = false;
    }
  }

  async save() {
    if (this.busy || !this.lastQuery || !this.assigned || this.invalidDrafts) return;
    this.busy = true;
    this.error = '';
    this.notice = '';
    const unificaciones = this.rows.flatMap(row => row.destination ? [{
      idProductoOrigen: row.idProducto, idEntidadOrigen: row.idEntidad,
      idProductoDestino: row.destination.idProducto,
    }] : []);
    try {
      const result = await firstValueFrom(this.http.post<{ unificados: number }>(
        '/api/productos/unificar', { unificaciones },
      ));
      this.notice = 'Unificaciones guardadas: ' + result.unificados + '.';
      const saved = new Set(unificaciones.map(row => row.idProductoOrigen));
      this.rows = this.rows.filter(row => !saved.has(row.idProducto));
      this.total = this.rows.length;
      // Recarga el resultado con los filtros que generaron la grilla guardada.
      await this.load(this.lastQuery);
    } catch (error) {
      this.error = this.message(error);
    } finally {
      this.busy = false;
    }
  }
}

bootstrapApplication(App, { providers: [provideHttpClient(), provideAnimationsAsync()] });

